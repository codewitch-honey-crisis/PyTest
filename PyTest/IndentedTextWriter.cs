using System.Text;

namespace PyTest;

class IndentedTextWriter : TextWriter
{
    bool _needIndent;
    readonly TextWriter _writer;
    public IndentedTextWriter(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _needIndent = false;
    }
    public TextWriter BaseWriter => _writer;
    public override Encoding Encoding => _writer.Encoding;
    public override void Write(char value)
    {
        if (_needIndent)
        {
            _writer.Write(DoIndent(IndentLevel));
            _needIndent = false;
        }
        if (value == '\n')
        {
            _needIndent = true;
            _writer.Write("\n");
        }
        else
            _writer.Write(value);
    }
    public int IndentLevel { get; set; } = 0;
    public string Indent { get; set; } = "    ";
    string DoIndent(int level)
    {
        if (level <= 0) { return ""; }

        var len = level * Indent.Length;
        var sb = new StringBuilder(len, len);

        for (var i = 0; i < level; ++i) { sb.Append(Indent); }

        return sb.ToString();
    }
}
