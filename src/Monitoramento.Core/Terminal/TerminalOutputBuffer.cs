using System.Text;

namespace Monitoramento.Core.Terminal;

/// <summary>
/// Saída de um terminal: decodifica UTF-8 em fluxo (um caractere dividido entre duas leituras não vira lixo) e guarda
/// os últimos <see cref="Capacity"/> caracteres num anel para o <c>termAttach</c>. <see cref="Seq"/> = total de
/// caracteres já produzidos (cresce sempre, mesmo depois de <see cref="Clear"/>): a UI descarta o que já veio.
/// </summary>
internal sealed class TerminalOutputBuffer
{
    /// <summary>1 Mi caracteres (anel de ~1 MB).</summary>
    public const int DefaultCapacity = 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly char[] _ring;
    private readonly Decoder _decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetDecoder();
    private int _start;
    private int _length;
    private long _seq;

    public TerminalOutputBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 16);
        _ring = new char[capacity];
    }

    public int Capacity => _ring.Length;

    public long Seq
    {
        get
        {
            lock (_gate)
            {
                return _seq;
            }
        }
    }

    /// <summary>Decodifica e guarda; devolve o texto novo (pode ser vazio, se só chegou parte de um caractere) e o seq.</summary>
    public (string Text, long Seq) Append(ReadOnlySpan<byte> bytes) => Decode(bytes, flush: false);

    /// <summary>Fim do fluxo: bytes pendentes de um caractere incompleto viram U+FFFD.</summary>
    public (string Text, long Seq) Flush() => Decode([], flush: true);

    /// <summary>Conteúdo recente e o seq do último caractere incluído.</summary>
    public (string Data, long Seq) Snapshot()
    {
        lock (_gate)
        {
            var chars = new char[_length];
            var first = Math.Min(_length, _ring.Length - _start);
            Array.Copy(_ring, _start, chars, 0, first);
            Array.Copy(_ring, 0, chars, first, _length - first);
            var skip = chars.Length > 0 && char.IsLowSurrogate(chars[0]) ? 1 : 0; // o anel cortou um par
            return (new string(chars, skip, chars.Length - skip), _seq);
        }
    }

    /// <summary>Esvazia o anel (reinício do terminal); o seq continua crescendo.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _start = 0;
            _length = 0;
            _decoder.Reset();
        }
    }

    private (string Text, long Seq) Decode(ReadOnlySpan<byte> bytes, bool flush)
    {
        lock (_gate)
        {
            var max = _decoder.GetCharCount(bytes, flush);
            if (max == 0)
            {
                if (flush)
                {
                    _decoder.Reset();
                }

                return (string.Empty, _seq);
            }

            Span<char> chars = max <= 4096 ? stackalloc char[max] : new char[max];
            var count = _decoder.GetChars(bytes, chars, flush);
            var text = chars[..count];
            Store(text);
            _seq += count;
            return (text.ToString(), _seq);
        }
    }

    private void Store(ReadOnlySpan<char> text)
    {
        if (text.Length >= _ring.Length)
        {
            text[^_ring.Length..].CopyTo(_ring);
            _start = 0;
            _length = _ring.Length;
            return;
        }

        foreach (var c in text)
        {
            var end = (_start + _length) % _ring.Length;
            _ring[end] = c;
            if (_length < _ring.Length)
            {
                _length++;
            }
            else
            {
                _start = (_start + 1) % _ring.Length;
            }
        }
    }
}
