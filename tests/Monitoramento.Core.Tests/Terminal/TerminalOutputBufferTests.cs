using System.Text;
using Monitoramento.Core.Terminal;

namespace Monitoramento.Core.Tests.Terminal;

/// <summary>Anel de saída do terminal: decodificação UTF-8 em fluxo, seq e corte.</summary>
public sealed class TerminalOutputBufferTests
{
    [Fact]
    public void Append_ReturnsTextAndCumulativeSeq()
    {
        var buffer = new TerminalOutputBuffer();

        var first = buffer.Append("oi "u8);
        var second = buffer.Append("mundo"u8);

        Assert.Equal(("oi ", 3L), first);
        Assert.Equal(("mundo", 8L), second);
        Assert.Equal(("oi mundo", 8L), buffer.Snapshot());
    }

    [Fact]
    public void Utf8CharacterSplitBetweenReads_IsDecodedOnce()
    {
        var buffer = new TerminalOutputBuffer();
        var bytes = Encoding.UTF8.GetBytes("ação 🚀");
        var cut = Array.IndexOf(bytes, (byte)0xC3) + 1; // no meio do "ç"
        var rocket = bytes.Length - 2; // no meio do emoji (4 bytes)

        var a = buffer.Append(bytes.AsSpan(0, cut));
        var b = buffer.Append(bytes.AsSpan(cut, rocket - cut));
        var c = buffer.Append(bytes.AsSpan(rocket));

        Assert.Equal("a", a.Text);
        Assert.Equal("ção ", b.Text);
        Assert.Equal("🚀", c.Text);
        Assert.Equal(a.Seq + 4, b.Seq);
        Assert.DoesNotContain('�', a.Text + b.Text + c.Text);
        Assert.Equal("ação 🚀", buffer.Snapshot().Data);
    }

    [Fact]
    public void Flush_TurnsIncompleteTailIntoReplacementCharacter()
    {
        var buffer = new TerminalOutputBuffer();
        buffer.Append([0x41, 0xE2, 0x82]); // "A" + 2 de 3 bytes do "€"

        var tail = buffer.Flush();

        Assert.Equal("�", tail.Text);
        Assert.Equal("A�", buffer.Snapshot().Data);
    }

    [Fact]
    public void Ring_KeepsOnlyTheLastCapacityCharacters_ButSeqCountsEverything()
    {
        var buffer = new TerminalOutputBuffer(capacity: 16);

        buffer.Append(Encoding.UTF8.GetBytes("0123456789"));
        buffer.Append(Encoding.UTF8.GetBytes("abcdefghij"));
        var (data, seq) = buffer.Snapshot();

        Assert.Equal("456789abcdefghij", data);
        Assert.Equal(20, seq);
    }

    [Fact]
    public void Ring_SingleAppendLargerThanCapacity_KeepsTheTail()
    {
        var buffer = new TerminalOutputBuffer(capacity: 16);

        buffer.Append(Encoding.UTF8.GetBytes(new string('x', 30) + "fim"));

        Assert.Equal(new string('x', 13) + "fim", buffer.Snapshot().Data);
        Assert.Equal(33, buffer.Seq);
    }

    [Fact]
    public void Ring_CutInsideSurrogatePair_DropsTheOrphanLowSurrogate()
    {
        var buffer = new TerminalOutputBuffer(capacity: 16);

        buffer.Append(Encoding.UTF8.GetBytes("🚀" + new string('y', 15))); // 2 + 15 = 17: sobra só a metade baixa

        var data = buffer.Snapshot().Data;
        Assert.Equal(new string('y', 15), data);
    }

    [Fact]
    public void Clear_EmptiesRing_SeqKeepsGrowing()
    {
        var buffer = new TerminalOutputBuffer();
        buffer.Append("antes"u8);

        buffer.Clear();
        var after = buffer.Append("depois"u8);

        Assert.Equal(("depois", 11L), buffer.Snapshot());
        Assert.Equal(11, after.Seq);
    }
}
