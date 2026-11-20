using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartGallery.AI.Florence;

// The pinned model uses ByteLevel BPE + RobertaProcessing. Only the fixed CAPTION prompt is encoded.
internal sealed class FlorenceByteBpeTokenizer
{
    private readonly Dictionary<string, int> _vocabulary = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _tokens = new();
    private readonly Dictionary<(string Left, string Right), int> _ranks = new();
    private readonly HashSet<int> _special = new();
    private readonly char[] _encodeBytes = new char[256];
    private readonly Dictionary<char, byte> _decodeBytes = new();
    private static readonly Regex Pieces = new(
        @"'(?:s|t|re|ve|m|ll|d)| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal FlorenceByteBpeTokenizer(Stream stream)
    {
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        var pre = root.GetProperty("pre_tokenizer");
        var model = root.GetProperty("model");
        if (pre.GetProperty("type").GetString() != "ByteLevel" ||
            pre.GetProperty("add_prefix_space").GetBoolean() ||
            !pre.GetProperty("use_regex").GetBoolean() || model.GetProperty("type").GetString() != "BPE" ||
            root.GetProperty("post_processor").GetProperty("type").GetString() != "RobertaProcessing")
        {
            throw new InvalidDataException("Nepodporovaný tokenizer Florence-2.");
        }
        foreach (var property in model.GetProperty("vocab").EnumerateObject())
        {
            AddToken(property.Name, property.Value.GetInt32());
        }
        foreach (var token in root.GetProperty("added_tokens").EnumerateArray())
        {
            var id = token.GetProperty("id").GetInt32();
            AddToken(token.GetProperty("content").GetString()!, id);
            if (token.GetProperty("special").GetBoolean())
            {
                _special.Add(id);
            }
        }
        var rank = 0;
        foreach (var merge in model.GetProperty("merges").EnumerateArray())
        {
            var pair = merge.GetString()!.Split(' ');
            if (pair.Length != 2)
            {
                throw new InvalidDataException("Neplatná dvojice BPE.");
            }
            _ranks.Add((pair[0], pair[1]), rank++);
        }
        var extra = 0;
        for (var value = 0; value < 256; value++)
        {
            var printable = value is >= 33 and <= 126 or >= 161 and <= 172 or >= 174 and <= 255;
            var character = (char)(printable ? value : 256 + extra++);
            _encodeBytes[value] = character;
            _decodeBytes.Add(character, (byte)value);
        }
        if (_vocabulary["<s>"] != 0 || _vocabulary["</s>"] != 2 || _vocabulary["<pad>"] != 1)
        {
            throw new InvalidDataException("Neplatné speciální tokeny Florence-2.");
        }
    }

    private void AddToken(string text, int id)
    {
        if ((_vocabulary.TryGetValue(text, out var existing) && existing != id) ||
            (_tokens.TryGetValue(id, out var previous) && previous != text))
        {
            throw new InvalidDataException("Nekonzistentní slovník tokenizeru.");
        }
        _vocabulary[text] = id;
        _tokens[id] = text;
    }

    internal long[] EncodeCaptionPrompt()
    {
        var ids = new List<long> { 0 };
        foreach (Match match in Pieces.Matches("What does the image describe?"))
        {
            var bytes = Encoding.UTF8.GetBytes(match.Value);
            var parts = bytes.Select(value => _encodeBytes[value].ToString()).ToList();
            while (parts.Count > 1)
            {
                var bestRank = int.MaxValue;
                var bestIndex = -1;
                for (var index = 0; index + 1 < parts.Count; index++)
                {
                    if (_ranks.TryGetValue((parts[index], parts[index + 1]), out var candidate) && candidate < bestRank)
                    {
                        bestRank = candidate;
                        bestIndex = index;
                    }
                }
                if (bestIndex < 0)
                {
                    break;
                }
                var left = parts[bestIndex];
                var right = parts[bestIndex + 1];
                for (var index = 0; index + 1 < parts.Count;)
                {
                    if (parts[index] == left && parts[index + 1] == right)
                    {
                        parts[index] += parts[index + 1];
                        parts.RemoveAt(index + 1);
                    }
                    else { index++; }
                }
            }
            ids.AddRange(parts.Select(part => (long)_vocabulary[part]));
        }
        ids.Add(2);
        return ids.ToArray();
    }

    internal string Decode(IEnumerable<long> ids)
    {
        var bytes = new List<byte>();
        foreach (var value in ids)
        {
            var id = checked((int)value);
            if (_special.Contains(id))
            {
                continue;
            }
            if (!_tokens.TryGetValue(id, out var text))
            {
                throw new InvalidDataException("Model vrátil neznámý token.");
            }
            foreach (var character in text)
            {
                if (!_decodeBytes.TryGetValue(character, out var b))
                {
                    throw new InvalidDataException("Neplatný byte-level token.");
                }
                bytes.Add(b);
            }
        }
        return new UTF8Encoding(false, true).GetString(bytes.ToArray()).Trim();
    }
}
