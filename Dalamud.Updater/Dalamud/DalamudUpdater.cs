#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Dalamud.Updater;
using Newtonsoft.Json;
using Serilog;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Dalamud
{
    public interface IDalamudLoadingOverlay
    {
        public enum DalamudUpdateStep
        {
            Dalamud,
            Assets,
            Runtime,
            Unavailable,
            Starting
        }

        public void SetStep(DalamudUpdateStep step);

        public void SetVisible();

        public void SetInvisible();

        public void ReportProgress(long? size, long downloaded, double? progress);
    }

    public class DalamudUpdater
    {
        private readonly DirectoryInfo addonDirectory;
        private readonly DirectoryInfo runtimeDirectory;
        private readonly DirectoryInfo assetDirectory;
        private readonly DirectoryInfo configDirectory;
        private readonly IUniqueIdCache? cache;
        private readonly string? githubToken;
        
        private readonly TimeSpan defaultTimeout = TimeSpan.FromMinutes(1);
        private bool forceProxy;
        
        public const string RuntimeVersion = "9.0.2";
        public static string OnlineHash { get; private set; } = string.Empty;
        public static string Version { get; private set; } = string.Empty;

        public DownloadState State { get; private set; } = DownloadState.Unknown;
        public Exception? EnsurementException { get; private set; }
        public DirectoryInfo Runtime => this.runtimeDirectory;
        public FileInfo? RunnerOverride { get; set; }
        public DirectoryInfo AssetDirectory { get; private set; }
        public IDalamudLoadingOverlay? Overlay { get; set; }
        
        private FileInfo runnerInternal;
        public FileInfo Runner
        {
            get => this.RunnerOverride ?? this.runnerInternal;
            private set => this.runnerInternal = value;
        }

        public enum DownloadState
        {
            Unknown,
            Done,
            NoIntegrity // fail with error message
        }

        public DalamudUpdater(DirectoryInfo addonDirectory, DirectoryInfo runtimeDirectory, DirectoryInfo assetDirectory, DirectoryInfo configDirectory, IUniqueIdCache? cache, string? githubToken)
        {
            this.addonDirectory = addonDirectory;
            this.runtimeDirectory = runtimeDirectory;
            this.assetDirectory = assetDirectory;
            this.configDirectory = configDirectory;
            this.cache = cache;
            this.githubToken = githubToken;
        }

        public void SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep progress)
        {
            this.Overlay!.SetStep(progress);
        }

        public void ShowOverlay()
        {
            this.Overlay!.SetVisible();
        }

        public void CloseOverlay()
        {
            this.Overlay!.SetInvisible();
        }

        private void ReportOverlayProgress(long? size, long downloaded, double? progress)
        {
            this.Overlay!.ReportProgress(size, downloaded, progress);
        }
        public void Run(bool overrideForceProxy = false)
        {
            Log.Information("[DUPDATE] 启动中... (是否强制使用代理: {ForceProxy})", overrideForceProxy);
            this.State = DownloadState.Unknown;
            this.forceProxy = overrideForceProxy;

            Task.Run(async () =>
            {
                const int MAX_TRIES = 10;
                var isUpdated = false;

                for (var tries = 0; tries < MAX_TRIES; tries++)
                {
                    try
                    {
                        await this.UpdateDalamud().ConfigureAwait(true);
                        isUpdated = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[DUPDATE] 更新失败, 重试 {TryCnt}/{MaxTries}...", tries, MAX_TRIES);
                        this.EnsurementException = ex;
                        this.forceProxy = false;
                    }
                }

                this.State = isUpdated ? DownloadState.Done : DownloadState.NoIntegrity;
            });
        }

        public async Task GetDalamudVersionInfoAsync()
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");

            try
            {
                var response = await httpClient.GetAsync(
                    "https://gh.atmoomen.top/https://raw.githubusercontent.com/Dalamud-DailyRoutines/ghapi-json-generator/output/v2/repos/AtmoOmen/Dalamud/releases/latest/data.json");
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(json);

                var version = jsonDoc.RootElement.GetProperty("tag_name").GetString();
                if (string.IsNullOrWhiteSpace(version))
                    throw new NullReferenceException("[DUPDATE] 未能找到对应的版本信息");
                Version = version!;
                
                var assets = jsonDoc.RootElement.GetProperty("assets");
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.GetProperty("name").GetString() == "hashes.json")
                    {
                        var downloadUrl = asset.GetProperty("browser_download_url").GetString();
                        if (string.IsNullOrWhiteSpace(downloadUrl)) continue;

                        var downloadPath = PlatformHelpers.GetTempFileName();

                        using (var fileResponse = await httpClient.GetAsync($"https://gh.atmoomen.top/{downloadUrl}", 
                                                                            HttpCompletionOption.ResponseHeadersRead))
                        {
                            fileResponse.EnsureSuccessStatusCode();
                            using (var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
                                await fileResponse.Content.CopyToAsync(fileStream);
                        }

                        var hash = ComputeFileHash(downloadPath);
                        File.Delete(downloadPath);
                        
                        Log.Information($"[DUPDATE] 获取到远端 Dalamud 哈希: {hash}");
                        OnlineHash = hash;
                        return;
                    }
                }

                throw new NullReferenceException("[DUPDATE] 未能找到对应的 hashes.json 文件");
            }
            catch (HttpRequestException e) { throw new Exception("访问 Github API 时发生错误: " + e.Message); }
            catch (TaskCanceledException) { throw new Exception("下载超时"); }
            catch (OperationCanceledException) { throw new Exception("下载取消"); }
        }

        private async Task UpdateDalamud()
        {
            try
            {
                Log.Information("[DUPDATE] 开始 Dalamud 更新进程");
                
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                await this.InitVersionInfoAsync();
                var paths = PreparePaths();
                await UpdateDalamudCoreAsync(paths.addonPath, paths.currentVersionPath);
                await UpdateRuntimeAsync(paths.runtimePaths);
                await UpdateAssetsAsync();

                // 最终验证
                if (!CheckDalamudIntegrity(paths.currentVersionPath))
                    throw new DalamudIntegrityException("完整性验证最终失败");

                this.Runner = new FileInfo(Path.Combine(paths.currentVersionPath.FullName, "Dalamud.Injector.exe"));
                this.SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Starting);
                this.ReportOverlayProgress(null, 0, null);

                Log.Information($"[DUPDATE] Dalamud {Version} ({OnlineHash}) 准备完毕");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] 更新 Dalamud 过程中发生错误");
                throw;
            }
        }

        private async Task InitVersionInfoAsync()
        {
            Log.Verbose("[DUPDATE] 开始检查版本信息");

            if (!string.IsNullOrWhiteSpace(OnlineHash) && !string.IsNullOrWhiteSpace(Version))
            {
                Log.Verbose("[DUPDATE] 版本信息已存在: {Version} ({Hash})", Version, OnlineHash);
                return;
            }

            Log.Information("[DUPDATE] 正在从 Github 获取最新版本信息");
            await GetDalamudVersionInfoAsync();
            Log.Information("[DUPDATE] 获取到版本: {Version} ({Hash})", Version, OnlineHash);
        }
        
        private (DirectoryInfo addonPath, DirectoryInfo currentVersionPath, DirectoryInfo[] runtimePaths) PreparePaths()
        {
            Log.Verbose("[DUPDATE] 开始准备路径信息");

            var addonPath = new DirectoryInfo(Path.Combine(this.addonDirectory.FullName, "Hooks"));
            var currentVersionPath = new DirectoryInfo(Path.Combine(addonPath.FullName, Version));

            var runtimePaths = new DirectoryInfo[]
            {
                new(Path.Combine(this.runtimeDirectory.FullName, "host", "fxr", RuntimeVersion)),
                new(Path.Combine(this.runtimeDirectory.FullName, "shared", "Microsoft.NETCore.App", RuntimeVersion)),
                new(Path.Combine(this.runtimeDirectory.FullName, "shared", "Microsoft.WindowsDesktop.App", RuntimeVersion))
            };

            Log.Verbose("[DUPDATE] 路径信息: 版本路径={Path}, 运行时路径数={Count}",
                        currentVersionPath.FullName, runtimePaths.Length);

            return (addonPath, currentVersionPath, runtimePaths);
        }
        
        private async Task UpdateDalamudCoreAsync(DirectoryInfo addonPath, DirectoryInfo currentVersionPath)
        {
            Log.Information("[DUPDATE] 开始检查 Dalamud 本体完整性");

            if (currentVersionPath.Exists && CheckDalamudIntegrity(currentVersionPath))
            {
                Log.Information("[DUPDATE] Dalamud 本体完整性检查已通过，无需更新");
                return;
            }

            Log.Information("[DUPDATE] Dalamud 本体完整性检查未通过, 开始更新");
            this.SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Dalamud);

            try
            {
                Log.Information("[DUPDATE] 开始下载 Dalamud 本体");
                await this.DownloadDalamud(currentVersionPath).ConfigureAwait(true);

                Log.Information("[DUPDATE] 清理旧版本 Dalamud 文件");
                CleanUpOld(addonPath, Version);

                this.cache?.Reset();
                Log.Information("[DUPDATE] Dalamud 本体更新完成");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] 下载 Dalamud 本体失败");
                throw new DalamudIntegrityException("下载 Dalamud 失败", ex);
            }
        }
        
        private async Task UpdateRuntimeAsync(DirectoryInfo[] runtimePaths)
        {
            Log.Information("[DUPDATE] 开始检查 .NET 运行时 {Version} 完整性", RuntimeVersion);

            if (!this.runtimeDirectory.Exists)
            {
                Log.Verbose("[DUPDATE] 运行时目录不存在, 进行创建");
                Directory.CreateDirectory(this.runtimeDirectory.FullName);
            }

            var versionFile = new FileInfo(Path.Combine(this.runtimeDirectory.FullName, "version"));
            var localVersion = this.GetLocalRuntimeVersion(versionFile);
            var runtimeNeedsUpdate = localVersion != RuntimeVersion;

            if (runtimePaths.All(p => p.Exists) && !runtimeNeedsUpdate)
            {
                Log.Information("[DUPDATE] .NET 运行时已是最新版本: {Version}", RuntimeVersion);
                return;
            }

            Log.Information("[DUPDATE] 需要更新 .NET 运行时: 本地={LocalVer}, 目标={RemoteVer}",
                            localVersion, RuntimeVersion);
            this.SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Runtime);

            try
            {
                Log.Information("[DUPDATE] 开始下载 .NET 运行时");
                await this.DownloadRuntime(this.runtimeDirectory, RuntimeVersion).ConfigureAwait(false);

                File.WriteAllText(versionFile.FullName, RuntimeVersion);
                Log.Information("[DUPDATE] .NET 运行时更新完成");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] .NET 运行时更新失败");
                throw new DalamudIntegrityException("无法确保运行时完整性", ex);
            }
        }
        
        private async Task UpdateAssetsAsync()
        {
            Log.Information("[DUPDATE] 开始验证资源文件完整性");
            
            this.SetOverlayProgress(IDalamudLoadingOverlay.DalamudUpdateStep.Assets);
            this.ReportOverlayProgress(null, 0, null);

            try
            {
                var assetResult = await AssetManager.EnsureAssets(this, this.assetDirectory).ConfigureAwait(true);
                this.AssetDirectory = assetResult;
                Log.Information("[DUPDATE] 资源文件验证完成: {Path}", this.AssetDirectory.FullName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] 资源文件验证失败");
                throw new DalamudIntegrityException("资源文件验证失败", ex);
            }
        }
        
        private static bool CanRead(FileInfo info)
        {
            try
            {
                using var stream = info.OpenRead();
                stream.ReadByte();
            }
            catch
            {
                return false;
            }

            return true;
        }
        
        public static bool CheckDalamudIntegrity(DirectoryInfo addonPath)
        {
            var files = addonPath.GetFiles();

            try
            {
                if (!CanRead(files.First(x => x.Name == "Dalamud.Injector.exe"))
                    || !CanRead(files.First(x => x.Name == "Dalamud.dll"))
                    || !CanRead(files.First(x => x.Name == "ImGuiScene.dll")))
                {
                    Log.Error("[DUPDATE] 无法打开核心文件");
                    return false;
                }

                var hashesPath = Path.Combine(addonPath.FullName, "hashes.json");
                if (!File.Exists(hashesPath))
                {
                    Log.Error("[DUPDATE] 无 hashes.json");
                    return false;
                }

                if (!string.IsNullOrEmpty(OnlineHash))
                {
                    var hashHash = ComputeFileHash(hashesPath);

                    if (OnlineHash != hashHash)
                    {
                        Log.Error($"[UPDATE] hashes.json 哈希比对不一致, 本地: {hashHash}, 远程: {OnlineHash}");
                        return false;
                    }
                }

                return CheckIntegrity(addonPath, File.ReadAllText(hashesPath));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] 无 Dalamud 完整性");
                return false;
            }
        }
        
        private static bool CheckIntegrity(DirectoryInfo directory, string hashesJson)
        {
            try
            {
                Log.Verbose("[DUPDATE] 开始检查目录 {Directory} 的完整性", directory.FullName);

                var hashes = JsonConvert.DeserializeObject<Dictionary<string, string>>(hashesJson);
                if (hashes == null) throw new ArgumentNullException(nameof(hashes));

                foreach (var hash in hashes)
                {
                    var file = Path.Combine(directory.FullName, hash.Key.Replace("\\", "/"));
                    var hashed = ComputeFileHash(file);

                    if (hashed != hash.Value)
                    {
                        Log.Error("[DUPDATE] 完整性检查失败: {0} ({1} - {2})", file, hash.Value, hashed);
                        return false;
                    }

                    Log.Verbose("[DUPDATE] 完整性检查通过: {0} ({1})", file, hashed);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] 完整性检查失败");
                return false;
            }

            return true;
        }
        
        private static string ComputeFileHash(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var md5 = MD5.Create();

                var hashHash = BitConverter.ToString(md5.ComputeHash(stream)).ToUpperInvariant().Replace("-", string.Empty);
                return hashHash;
            }
            catch (Exception e)
            {
                throw new Exception("Error computing file hash: " + e.Message);
            }
        }

        private static void CleanUpOld(DirectoryInfo addonPath, string currentVer)
        {
            if (!addonPath.Exists)
                return;

            foreach (var directory in addonPath.GetDirectories())
            {
                if (directory.Name == "dev" || directory.Name == currentVer) continue;

                try
                {
                    directory.Delete(true);
                }
                catch
                {
                    // ignored
                }
            }
        }

        private async Task DownloadDalamud(DirectoryInfo addonPath)
        {
            const string REPO_API = "https://api.github.com/repos/AtmoOmen/Dalamud/releases/latest";

            if (addonPath.Exists) addonPath.Delete(true);
            addonPath.Create();

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("XIVLauncherCN");
            if (!string.IsNullOrWhiteSpace(this.githubToken)) 
                httpClient.DefaultRequestHeaders.Authorization = new("Bearer", this.githubToken);

            try
            {
                var response = await httpClient.GetAsync(REPO_API);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                using var jsonDoc = JsonDocument.Parse(json);
                var assets = jsonDoc.RootElement.GetProperty("assets");

                var downloadPath = PlatformHelpers.GetTempFileName();
                
                foreach (var asset in assets.EnumerateArray())
                {
                    var fileName = asset.GetProperty("name").GetString()!;
                    var downloadUrl = asset.GetProperty("browser_download_url").GetString()!;

                    if (fileName != "latest.7z") continue;
                    
                    await this.DownloadFile($"https://gh.atmoomen.top/{downloadUrl}", downloadPath, this.defaultTimeout).ConfigureAwait(false);
                    PlatformHelpers.Un7za(downloadPath, addonPath.FullName);
                    File.Delete(downloadPath);
                    break;
                }
                
                try
                {
                    var devPath = new DirectoryInfo(Path.Combine(addonPath.FullName, "..", "dev"));
                    PlatformHelpers.DeleteAndRecreateDirectory(devPath);
                    PlatformHelpers.CopyFilesRecursively(addonPath, devPath);
                }
                catch (Exception ex) { Log.Error(ex, "[DUPDATE] 复制到 dev 目录失败"); }
            }
            catch (HttpRequestException e)
            {
                Log.Error(e, "[DUPDATE] GitHub API 请求失败");
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] 下载过程中发生错误");
                throw;
            }
        }

        private async Task DownloadRuntime(DirectoryInfo runtimePath, string version)
        {
            if (runtimePath.Exists) runtimePath.Delete(true);
            runtimePath.Create();

            try
            {
                // 微软 .NET 运行时下载链接
                var dotnetRuntimeUrl = $"https://dotnetcli.azureedge.net/dotnet/Runtime/{version}/dotnet-runtime-{version}-win-x64.zip";
                var desktopRuntimeUrl = $"https://dotnetcli.azureedge.net/dotnet/WindowsDesktop/{version}/windowsdesktop-runtime-{version}-win-x64.zip";

                var downloadPath = PlatformHelpers.GetTempFileName();

                if (File.Exists(downloadPath))
                    File.Delete(downloadPath);

                // 下载 .NET 运行时
                Log.Verbose("[DUPDATE] 正在下载 .NET 运行时 v{Version}...", version);
                await this.DownloadFile(dotnetRuntimeUrl, downloadPath, this.defaultTimeout).ConfigureAwait(false);
                ZipFile.ExtractToDirectory(downloadPath, runtimePath.FullName);

                // 下载 Windows Desktop 运行时
                Log.Verbose("[DUPDATE] 正在下载 .NET 桌面运行时 v{Version}...", version);
                await this.DownloadFile(desktopRuntimeUrl, downloadPath, this.defaultTimeout).ConfigureAwait(false);
                ZipFile.ExtractToDirectory(downloadPath, runtimePath.FullName);

                File.Delete(downloadPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DUPDATE] 从微软下载 .NET 运行时 v{Version} 时失败", version);
                throw;
            }
        }

        private string GetLocalRuntimeVersion(FileInfo versionFile)
        {
            const string DEFAULT_VERSION = "5.0.0";

            try
            {
                if (versionFile.Exists)
                    return File.ReadAllText(versionFile.FullName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[DUPDATE] 无法读取本地运行时版本, 返回默认版本 {DEFAULT_VERSION}");
            }

            return DEFAULT_VERSION;
        }
        
        public async Task DownloadFile(string url, string path, TimeSpan timeout)
        {
            if (this.forceProxy && url.Contains("/File/Get/")) url = url.Replace("/File/Get/", "/File/GetProxy/");

            using var downloader = new HttpClientDownloadWithProgress(url, path);
            downloader.ProgressChanged += this.ReportOverlayProgress;

            await downloader.Download(timeout).ConfigureAwait(false);
        }
    }
    
    public class DalamudIntegrityException : Exception
    {
        public DalamudIntegrityException(string msg, Exception? inner = null) : base(msg, inner) { }
    }
    
    public interface IUniqueIdCache
    {
        void Reset();
    }
}
