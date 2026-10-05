using System.Collections.Generic;
using System.Text;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Splits a TCP byte stream into complete JSON messages. Clients send bare JSON with no
    /// delimiter or length prefix, so a message ends when its outermost brace or bracket closes.
    /// TCP may split one message across many reads or join several in one read.
    /// </summary>
    internal sealed class JsonMessageFramer
    {
        // Keeps a multi-byte UTF-8 character that straddles two reads intact.
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _pending = new StringBuilder();
        private int _depth;
        private bool _inString;
        private bool _escaped;

        public List<string> Append(byte[] buffer, int count)
        {
            char[] chars = new char[_decoder.GetCharCount(buffer, 0, count)];
            int charCount = _decoder.GetChars(buffer, 0, count, chars, 0);

            var messages = new List<string>();
            for (int i = 0; i < charCount; i++)
            {
                char c = chars[i];

                if (_inString)
                {
                    _pending.Append(c);
                    if (_escaped) _escaped = false;
                    else if (c == '\\') _escaped = true;
                    else if (c == '"') _inString = false;
                    continue;
                }

                switch (c)
                {
                    case '"':
                        _inString = true;
                        _pending.Append(c);
                        break;
                    case '{':
                    case '[':
                        _depth++;
                        _pending.Append(c);
                        break;
                    case '}':
                    case ']':
                        _pending.Append(c);
                        if (_depth > 0 && --_depth == 0) Flush(messages);
                        break;
                    default:
                        if (_depth > 0 || _pending.Length > 0 || !char.IsWhiteSpace(c)) _pending.Append(c);
                        break;
                }
            }

            // Text outside any object (not JSON-RPC) is handed on as is when the read ends, so the
            // client gets a parse error instead of waiting for its timeout.
            if (_depth == 0 && !_inString && _pending.Length > 0) Flush(messages);

            return messages;
        }

        private void Flush(List<string> messages)
        {
            messages.Add(_pending.ToString());
            _pending.Clear();
        }
    }
}
