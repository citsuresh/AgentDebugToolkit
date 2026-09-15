using System.Text;

namespace AgentDebugToolkit.ConsoleAutomation.Cli;

/// <summary>Small VT100 subset parser for normal text, CR/LF/BS/TAB, and CSI cursor/erase codes.</summary>
internal sealed class TerminalBuffer
{
    private readonly object sync = new();
    private readonly char[][] cells;
    private int row;
    private int column;
    private bool inEscape;
    private bool inCsi;
    private bool inOsc;
    private readonly StringBuilder escape = new();

    public TerminalBuffer(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
        cells = Enumerable.Range(0, rows).Select(_ => Enumerable.Repeat(' ', columns).ToArray()).ToArray();
    }

    public int Columns { get; }
    public int Rows { get; }

    public void Write(string text)
    {
        lock (sync)
        {
            foreach (var character in text)
            {
                if (inEscape)
                {
                    escape.Append(character);
                    if (!inCsi && character == '[')
                    {
                        inCsi = true;
                        continue;
                    }

                    if (!inCsi && character == ']')
                    {
                        inOsc = true;
                        continue;
                    }

                    if (inOsc)
                    {
                        if (character == '\a' || character == '\x1b')
                        {
                            escape.Clear();
                            inEscape = false;
                            inOsc = false;
                        }
                        continue;
                    }

                    if ((!inCsi || character >= '@' && character <= '~') || escape.Length > 64)
                    {
                        HandleEscape(escape.ToString());
                        escape.Clear();
                        inEscape = false;
                        inCsi = false;
                    }
                    continue;
                }

                if (character == '\x1b')
                {
                    inEscape = true;
                    inCsi = false;
                    inOsc = false;
                    continue;
                }

                switch (character)
                {
                    case '\r': column = 0; break;
                    case '\n': NewLine(); break;
                    case '\b': column = Math.Max(0, column - 1); break;
                    case '\t': column = Math.Min(Columns - 1, ((column / 8) + 1) * 8); break;
                    case var _ when !char.IsControl(character): Put(character); break;
                }
            }
        }
    }

    public string[] Snapshot(int? lastLines = null)
    {
        lock (sync)
        {
            var lines = cells.Select(line => new string(line).TrimEnd()).ToArray();
            return lastLines is int count ? lines.TakeLast(Math.Max(0, count)).ToArray() : lines;
        }
    }

    private void Put(char character)
    {
        cells[row][column] = character;
        if (++column == Columns)
        {
            column = 0;
            NewLine();
        }
    }

    private void NewLine()
    {
        if (++row < Rows) return;
        var recycledRow = cells[0];
        Array.Copy(cells, 1, cells, 0, Rows - 1);
        cells[Rows - 1] = recycledRow;
        Array.Fill(recycledRow, ' ');
        row = Rows - 1;
    }

    private void HandleEscape(string sequence)
    {
        if (!sequence.StartsWith('[')) return;
        var command = sequence[^1];
        var parameters = sequence[1..^1].Split(';')
            .Select(value => int.TryParse(value, out var result) ? result : 0).ToArray();
        var first = parameters.Length == 0 || parameters[0] == 0 ? 1 : parameters[0];

        switch (command)
        {
            case 'A': row = Math.Max(0, row - first); break;
            case 'B': row = Math.Min(Rows - 1, row + first); break;
            case 'C': column = Math.Min(Columns - 1, column + first); break;
            case 'D': column = Math.Max(0, column - first); break;
            case 'H':
            case 'f':
                row = Math.Clamp((parameters.ElementAtOrDefault(0) == 0 ? 1 : parameters[0]) - 1, 0, Rows - 1);
                column = Math.Clamp((parameters.ElementAtOrDefault(1) == 0 ? 1 : parameters[1]) - 1, 0, Columns - 1);
                break;
            case 'J':
                if (parameters.ElementAtOrDefault(0) is 0 or 2)
                {
                    foreach (var line in cells) Array.Fill(line, ' ');
                    if (parameters.ElementAtOrDefault(0) == 2) row = column = 0;
                }
                break;
            case 'K':
                Array.Fill(cells[row], ' ', column, Columns - column);
                break;
        }
    }
}
