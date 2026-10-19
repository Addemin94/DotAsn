using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Asn1.UnitTests;

/// <summary>Runs the real asn1 executable and checks output and exit codes.</summary>
public class CliTests
{
    private static readonly string s_telemetry = Path.Combine(AppContext.BaseDirectory, "samples", "telemetry", "telemetry.asn");

    private static (int Exit, string Out, string Err) Run(string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null, WorkingDirectory = AppContext.BaseDirectory };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "asn1.dll"));
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        if (stdin is not null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }

        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit(60_000);
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    [Fact]
    public void VersionAndStandards()
    {
        Assert.Contains("asn1 (DotAsn)", Run(null, "version").Out);
        var standards = Run(null, "standards").Out;
        Assert.Contains("UPER", standards);
        Assert.Contains("not implemented", standards);
    }

    [Fact]
    public void EncodeDecodeRoundTrip()
    {
        var encode = Run(null, "encode", s_telemetry, "-t", "Telemetry-Protocol.Heartbeat", "-f", "uper", "--value", """{"sequenceNumber":7,"uptimeSeconds":4000000000}""");
        Assert.Equal(0, encode.Exit);
        Assert.Equal("07EE6B2800", encode.Out.Trim());
        var decode = Run("07EE6B2800", "decode", s_telemetry, "-t", "Heartbeat", "-f", "uper", "--input-format", "hex");
        Assert.Equal(0, decode.Exit);
        using var json = JsonDocument.Parse(decode.Out);
        Assert.Equal(4000000000, json.RootElement.GetProperty("uptimeSeconds").GetInt64());
    }

    [Fact]
    public void JerIsWrittenAsText()
    {
        var encode = Run(null, "encode", s_telemetry, "-t", "Heartbeat", "-f", "jer", "--value", """{"sequenceNumber":7,"uptimeSeconds":4000000000}""");
        Assert.Equal(0, encode.Exit);
        using var json = JsonDocument.Parse(encode.Out);
        Assert.Equal(4000000000, json.RootElement.GetProperty("uptimeSeconds").GetInt64());
    }

    [Fact]
    public void XerIsWrittenAsText()
    {
        var encode = Run(null, "encode", s_telemetry, "-t", "Heartbeat", "-f", "cxer", "--value", """{"sequenceNumber":7,"uptimeSeconds":4000000000}""");
        Assert.Equal(0, encode.Exit);
        Assert.Equal("<Heartbeat><sequenceNumber>7</sequenceNumber><uptimeSeconds>4000000000</uptimeSeconds></Heartbeat>", encode.Out.Trim());
    }

    [Fact]
    public void ExitCodes()
    {
        Assert.Equal(1, Run(null, "validate-message", s_telemetry, "-t", "Heartbeat", "-f", "uper", "--hex", "07EE").Exit);
        Assert.Equal(0, Run(null, "validate-message", s_telemetry, "-t", "Heartbeat", "-f", "uper", "--hex", "07EE6B2800").Exit);
        Assert.Equal(3, Run(null, "encode", s_telemetry, "-t", "Heartbeat", "-f", "exer", "--value", """{"sequenceNumber":1,"uptimeSeconds":2}""").Exit);
        Assert.Equal(2, Run(null, "encode", s_telemetry, "-t", "Heartbeat", "-f", "der", "--value", """{"sequenceNumber":1,"uptimeSeconds":2}""", "--output-format", "text").Exit);
        Assert.Equal(2, Run(null, "encode", s_telemetry, "-t", "Heartbeat", "-f", "nonsense", "--value", """{"sequenceNumber":1,"uptimeSeconds":2}""").Exit);
        Assert.Equal(1, Run(null, "encode", s_telemetry, "-t", "Heartbeat", "-f", "der", "--value", """{"sequenceNumber":999,"uptimeSeconds":0}""").Exit);
        Assert.Equal(1, Run(null, "check", "does-not-exist.asn").Exit);
    }

    [Fact]
    public void LintProducesSarifAndJson()
    {
        var dir = Directory.CreateTempSubdirectory("asn1cli");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "bad.asn"), "M DEFINITIONS ::= BEGIN A ::= Missing END");
            var sarif = Run(null, "lint", dir.FullName, "--format", "sarif");
            Assert.Equal(1, sarif.Exit);
            using (var doc = JsonDocument.Parse(sarif.Out))
            {
                Assert.Equal("2.1.0", doc.RootElement.GetProperty("version").GetString());
            }

            var json = Run(null, "check", dir.FullName, "--format", "json");
            using var j = JsonDocument.Parse(json.Out);
            Assert.Equal("ASN2001", j.RootElement.GetProperty("diagnostics")[0].GetProperty("id").GetString());
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void GenerateCSharpWritesFilesAndManifest()
    {
        var dir = Directory.CreateTempSubdirectory("asn1gen");
        try
        {
            var r = Run(null, "generate-csharp", s_telemetry, "-o", dir.FullName, "-n", "Test.Ns", "--quiet");
            Assert.Equal(0, r.Exit);
            Assert.True(File.Exists(Path.Combine(dir.FullName, "TelemetryProtocol.g.cs")));
            Assert.Contains("namespace Test.Ns;", File.ReadAllText(Path.Combine(dir.FullName, "TelemetryProtocol.g.cs")));
            Assert.True(File.Exists(Path.Combine(dir.FullName, "asn1-generated.manifest.json")));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void InspectDumpsDer()
    {
        var r = Run(null, "inspect", "-f", "der", "--hex", "3009800107810400EE6B28");
        Assert.Equal(0, r.Exit);
        Assert.Contains("SEQUENCE", r.Out);
    }
}
