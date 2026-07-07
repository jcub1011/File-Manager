using FileManager.Platform.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace FileManager.Platform.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsIpcEndpointProviderTests
{
    [Fact]
    public async Task Accepts_a_CurrentUserOnly_client_connection()
    {
        string pipeName = "fm-test-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("FILEMANAGER_PIPE_NAME", pipeName);
        try
        {
            WindowsIpcEndpointProvider provider = new(NullLogger<WindowsIpcEndpointProvider>.Instance);
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

            Task<FileManager.Contracts.Primitives.Result<Stream, string>> accept = provider.AcceptAsync(cts.Token);

            await using NamedPipeClientStream client = new(
                ".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(cts.Token);

            var accepted = await accept;
            Assert.True(accepted.TryGetValue(out Stream? server));

            // Prove the duplex stream actually moves bytes.
            byte[] hello = [1, 2, 3];
            await client.WriteAsync(hello, cts.Token);
            await client.FlushAsync(cts.Token);
            byte[] received = new byte[3];
            await server!.ReadExactlyAsync(received, cts.Token);
            Assert.Equal(hello, received);

            await server.DisposeAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("FILEMANAGER_PIPE_NAME", null);
        }
    }

    [Fact]
    public void Pipe_name_derivation_matches_the_contracts_side()
    {
        // The §4.11 flagged duplication: both derivations must agree byte-for-byte.
        Assert.Equal(
            FileManager.Contracts.IPC.IpcEndpoint.Resolve(),
            WindowsIpcEndpointProvider.ResolvePipeName());
    }
}
