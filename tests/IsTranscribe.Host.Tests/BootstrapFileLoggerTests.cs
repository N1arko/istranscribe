using System.Text.Json;
using IsTranscribe.Application.Diagnostics;
using Xunit;

namespace IsTranscribe.Host.Tests;

/// <summary>
/// @spec spec://modules/platform/INFRA-004-diagnostics-jobs-and-recovery#diagnostics.events
/// </summary>
public sealed class BootstrapFileLoggerTests
{
    [Fact]
    public void LogEvent_WritesStructuredJsonLine()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var logPath = Path.Combine(root, "logs", "host.log");
            var logger = new BootstrapFileLogger(logPath);

            logger.LogEvent(
                level: "Info",
                eventCode: "APP_START",
                message: "Host bootstrap started.",
                jobName: "Bootstrap",
                metadata: new Dictionary<string, object?>
                {
                    ["surface"] = "MainShell"
                });

            var line = File.ReadAllLines(logPath).Single();
            using var doc = JsonDocument.Parse(line);
            var rootElement = doc.RootElement;

            Assert.Equal("Info", rootElement.GetProperty("level").GetString());
            Assert.Equal("APP_START", rootElement.GetProperty("event_code").GetString());
            Assert.Equal("Host bootstrap started.", rootElement.GetProperty("message").GetString());
            Assert.Equal("Bootstrap", rootElement.GetProperty("job_name").GetString());
            Assert.Equal("MainShell", rootElement.GetProperty("metadata").GetProperty("surface").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TemporaryExternalReadLockDoesNotInterruptTheRuntimeCaller()
    {
        var root = JsonApplicationSettingsStoreTestsHelpers.CreateRoot();

        try
        {
            var logPath = Path.Combine(root, "logs", "host.log");
            var logger = new BootstrapFileLogger(logPath);
            logger.Info("before lock");

            using (File.Open(logPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var exception = Record.Exception(() => logger.Info("blocked write"));
                Assert.Null(exception);
            }

            logger.Info("after lock");
            var lines = File.ReadAllLines(logPath);
            Assert.Equal(2, lines.Length);
            Assert.DoesNotContain(lines, static line => line.Contains("blocked write", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
