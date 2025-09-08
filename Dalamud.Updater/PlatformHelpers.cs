using System;
using System.IO;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace XIVLauncher.Common.PlatformAbstractions
{
    public static class PlatformHelpers
    {
        public static string GetTempFileName()
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        }

        public static void Un7za(string archivePath, string destinationPath)
        {
            using var archive = SevenZipArchive.Open(archivePath);
            var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                if (!reader.Entry.IsDirectory)
                    reader.WriteEntryToDirectory(destinationPath, new ExtractionOptions() 
                    { 
                        ExtractFullPath = true, 
                        Overwrite = true 
                    });
            }
        }

        public static void DeleteAndRecreateDirectory(DirectoryInfo dir)
        {
            if (!dir.Exists)
            {
                dir.Create();
            }
            else
            {
                dir.Delete(true);
                dir.Create();
            }
        }

        public static void CopyFilesRecursively(DirectoryInfo source, DirectoryInfo target)
        {
            foreach (var dir in source.GetDirectories())
                CopyFilesRecursively(dir, target.CreateSubdirectory(dir.Name));

            foreach (var file in source.GetFiles())
                file.CopyTo(Path.Combine(target.FullName, file.Name));
        }
    }
}