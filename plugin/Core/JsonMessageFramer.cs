using System.Text;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Rebuilds whole JSON messages from a TCP byte stream. A request larger than one socket read
    /// (or a multi-byte UTF-8 character split between reads) arrives in pieces; this keeps the
    /// pending text and hands out one complete top-level JSON value at a time.
    /// </summary>
    public class JsonMessageFramer
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _pending = new StringBuilder();

        // Scan state, kept between reads so each character is examined only once.
        private int _scanned;
        private int _depth;
        private bool _inString;
        private bool _escaped;
        private bool _started;

        public void Append(byte[] buffer, int count)
        {
            char[] chars = new char[_decoder.GetCharCount(buffer, 0, count)];
            int n = _decoder.GetChars(buffer, 0, count, chars, 0);
            _pending.Append(chars, 0, n);
        }

        /// <summary>
        /// Returns true and the next complete message when one is buffered. Text that does not start
        /// with '{' or '[' is returned as is, so the caller answers it with a parse error.
        /// </summary>
        public bool TryTake(out string message)
        {
            message = null;
            for (; _scanned < _pending.Length; _scanned++)
            {
                char c = _pending[_scanned];
                if (!_started)
                {
                    if (char.IsWhiteSpace(c))
                    {
                        // Separator between messages: drop it so a message never starts with blanks.
                        _pending.Remove(0, 1);
                        _scanned--;
                        continue;
                    }
                    if (c != '{' && c != '[')
                    {
                        message = _pending.ToString();
                        Reset(_pending.Length);
                        return true;
                    }
                    _started = true;
                }

                if (_inString)
                {
                    if (_escaped) _escaped = false;
                    else if (c == '\\') _escaped = true;
                    else if (c == '"') _inString = false;
                    continue;
                }

                if (c == '"') _inString = true;
                else if (c == '{' || c == '[') _depth++;
                else if (c == '}' || c == ']')
                {
                    _depth--;
                    if (_depth == 0)
                    {
                        int end = _scanned + 1;
                        message = _pending.ToString(0, end);
                        Reset(end);
                        return true;
                    }
                }
            }
            return false;
        }

        private void Reset(int consumed)
        {
            _pending.Remove(0, consumed);
            _scanned = 0;
            _depth = 0;
            _inString = false;
            _escaped = false;
            _started = false;
        }
    }
}
