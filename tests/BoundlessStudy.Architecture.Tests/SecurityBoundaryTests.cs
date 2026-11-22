using System.Buffers.Binary;
using System.Text;
using BoundlessStudy.Contracts.NativeMessaging;

namespace BoundlessStudy.Architecture.Tests;

/// <summary>Independent regression checks for the browser and persistence trust boundaries.</summary>
public sealed class SecurityBoundaryTests
{
    [Fact]
    public async Task Native_frame_reader_rejects_zero_and_oversized_lengths_before_payload_read()
    {
        foreach (var length in new[] { 0, -1, NativeMessagingProtocol.MaxMessageBytes + 1 })
        {
            var frame = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(frame, length);
            await Assert.ThrowsAsync<InvalidDataException>(() => NativeFrameCodec.ReadMessageAsync(new MemoryStream(frame)));
        }
    }

    [Fact]
    public async Task Native_frame_reader_accepts_maximum_length_json_and_handles_fragmented_prefix()
    {
        var json = "{\"type\":\"hello\",\"padding\":\"" + new string('x', NativeMessagingProtocol.MaxMessageBytes - 29) + "\"}";
        var payload = Encoding.UTF8.GetBytes(json);
        Assert.Equal(NativeMessagingProtocol.MaxMessageBytes, payload.Length);
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);

        var parsed = await NativeFrameCodec.ReadMessageAsync(new FragmentedReadStream(frame));
        Assert.NotNull(parsed);
        Assert.Equal("hello", parsed.Type);
    }

    [Fact]
    public void Only_agent_sources_may_reference_sqlite_or_database_provider()
    {
        var sourceRoot = Path.Combine(FindRepoRoot(), "src");
        foreach (var project in new[] { "BoundlessStudy.Desktop", "BoundlessStudy.Service", "BoundlessStudy.Bridge" })
        {
            var root = Path.Combine(sourceRoot, project);
            var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .Where(path => {
                    var text = File.ReadAllText(path);
                    return text.Contains("Microsoft.Data.Sqlite", StringComparison.Ordinal)
                        || text.Contains("SqliteConnection", StringComparison.Ordinal)
                        || text.Contains("SqliteDataStore", StringComparison.Ordinal);
                }).ToArray();
            Assert.Empty(offenders);
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BoundlessStudy.slnx"))
                || File.Exists(Path.Combine(current.FullName, "BoundlessStudy.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate solution root.");
    }

    private sealed class FragmentedReadStream(byte[] data) : MemoryStream
    {
        private int _position;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= data.Length) return ValueTask.FromResult(0);
            var take = Math.Min(Math.Min(buffer.Length, 2), data.Length - _position);
            data.AsMemory(_position, take).CopyTo(buffer);
            _position += take;
            return ValueTask.FromResult(take);
        }
    }
}
