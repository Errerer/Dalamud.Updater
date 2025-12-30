using System;
using System.IO;
using System.Windows.Forms;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace Dalamud.Updater
{
    public class TextBoxSink : ILogEventSink
    {
        private readonly TextBox textBox;
        private readonly ITextFormatter formatter;

        public TextBoxSink(TextBox textBox, string outputTemplate)
        {
            this.textBox = textBox ?? throw new ArgumentNullException(nameof(textBox));
            this.formatter = new MessageTemplateTextFormatter(outputTemplate);
        }

        public void Emit(LogEvent logEvent)
        {
            if (logEvent == null)
                throw new ArgumentNullException(nameof(logEvent));

            var sw = new StringWriter();
            this.formatter.Format(logEvent, sw);
            var message = sw.ToString();

            if (this.textBox.InvokeRequired)
            {
                this.textBox.Invoke(new Action(() =>
                {
                    AppendText(message);
                }));
            }
            else
            {
                AppendText(message);
            }
        }

        private void AppendText(string message)
        {
            this.textBox.AppendText(message);
            this.textBox.SelectionStart = this.textBox.Text.Length;
            this.textBox.ScrollToCaret();

            // 限制日志行数，防止内存占用过大
            if (this.textBox.Lines.Length > 1000)
            {
                var lines = this.textBox.Lines;
                var newLines = new string[500];
                Array.Copy(lines, lines.Length - 500, newLines, 0, 500);
                this.textBox.Lines = newLines;
            }
        }
    }

    public static class TextBoxSinkExtensions
    {
        public static LoggerConfiguration TextBox(
            this LoggerSinkConfiguration sinkConfiguration,
            TextBox textBox,
            LogEventLevel restrictedToMinimumLevel = LogEventLevel.Verbose,
            string outputTemplate = "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
        {
            if (sinkConfiguration == null)
                throw new ArgumentNullException(nameof(sinkConfiguration));
            if (textBox == null)
                throw new ArgumentNullException(nameof(textBox));

            return sinkConfiguration.Sink(new TextBoxSink(textBox, outputTemplate), restrictedToMinimumLevel);
        }
    }
}
