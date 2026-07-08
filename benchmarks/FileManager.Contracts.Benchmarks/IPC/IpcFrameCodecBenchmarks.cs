using BenchmarkDotNet.Attributes;
using FileManager.Contracts.DryRun;
using FileManager.Contracts.IPC;
using FileManager.Contracts.Primitives;

namespace FileManager.Contracts.Benchmarks.IPC;

/// <summary>Measures the IPC wire path both peers share: source-generated JSON
/// (<see cref="IpcSerializer"/>) and length-prefixed framing (<see cref="IpcFrameCodec"/>).
/// Uses a <see cref="DryRunResponse"/> because dry-run reports are the largest payloads on the
/// wire — the case that stresses serialization and approaches the 16 MiB frame cap.</summary>
[MemoryDiagnoser]
public class IpcFrameCodecBenchmarks
{
    private DryRunResponse _response = null!;
    private byte[] _payload = null!;
    private readonly MemoryStream _stream = new();

    /// <summary>Files in the dry-run report; drives payload size. 20k reports run several MiB,
    /// exercising the large-frame path the oversized-response test guards.</summary>
    [Params(10, 1_000, 20_000)]
    public int FileCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        List<DryRunFileResult> files = new(FileCount);
        for (int i = 0; i < FileCount; i++)
        {
            files.Add(new DryRunFileResult
            {
                SourcePath = $@"C:\src\dir{i % 16}\sub{i % 8}\file-{i}.dat",
                Disposition = DryRunFileDisposition.WouldProcess,
                Targets =
                [
                    new DryRunTargetAction
                    {
                        TargetPath = $@"C:\dst\dir{i % 16}\file-{i}.dat",
                        Kind = DryRunTargetKind.WouldWrite,
                        Detail = null,
                    },
                ],
                SourceDisposition = "KeepSource",
            });
        }

        DryRunReport report = new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, files);
        _response = new DryRunResponse { Report = report };
        _payload = IpcSerializer.SerializeResponse(_response);
    }

    [Benchmark]
    public int Serialize() => IpcSerializer.SerializeResponse(_response).Length;

    [Benchmark]
    public bool Deserialize()
    {
        Result<IpcResponse, string> result = IpcSerializer.DeserializeResponse(_payload);
        return result.TryGetValue(out _);
    }

    /// <summary>One write + one read of the framed payload over an in-memory stream — isolates the
    /// header/length codec from real pipe I/O.</summary>
    [Benchmark]
    public async Task<int> FrameRoundTrip()
    {
        _stream.Position = 0;
        _stream.SetLength(0);

        await IpcFrameCodec.WriteFrameAsync(_stream, _payload);
        _stream.Position = 0;

        Result<byte[], string> read = await IpcFrameCodec.ReadFrameAsync(_stream);
        read.TryGetValue(out byte[]? frame);
        return frame?.Length ?? 0;
    }
}
