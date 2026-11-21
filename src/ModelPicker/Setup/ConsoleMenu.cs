namespace ModelPicker.Setup;

/// <summary>
/// Arrow-key menu for the console, drawn in place: up/down (or a digit) moves the highlight,
/// Enter confirms, Esc cancels. Same look as the provider menu in chat.ps1.
/// </summary>
public static class ConsoleMenu
{
    /// <summary>False when input or output is redirected (e.g. answers piped in by a test): use a typed answer instead.</summary>
    public static bool Available => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public const string Hint = "(strzałki: wybór, Enter: zatwierdź, Esc: anuluj)";

    /// <summary>Returns the chosen index, or -1 when cancelled with Esc.</summary>
    public static int Select(IReadOnlyList<(string Text, ConsoleColor Color)> items, int selected)
    {
        int width = Width();
        int top = -1;
        bool cursorVisible = true;
        try { cursorVisible = OperatingSystem.IsWindows() ? Console.CursorVisible : true; } catch (IOException) { }
        ConsoleCancelEventHandler restore = (_, _) => ShowCursor(true);
        Console.CancelKeyPress += restore;
        ShowCursor(false);
        try
        {
            while (true)
            {
                if (top >= 0) Console.SetCursorPosition(0, top);
                for (int i = 0; i < items.Count; i++)
                {
                    bool current = i == selected;
                    var line = ((current ? "  > " : "    ") + items[i].Text).PadRight(width);
                    if (line.Length > width) line = line[..width]; // a wrapped line would shift the redraw
                    Console.ForegroundColor = current ? ConsoleColor.Black : items[i].Color;
                    if (current) Console.BackgroundColor = ConsoleColor.Cyan;
                    Console.Write(line);
                    Console.ResetColor();
                    Console.WriteLine();
                }
                // Measured after drawing: if the window scrolled while drawing, the menu moved up with it.
                if (top < 0) top = Console.CursorTop - items.Count;

                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow: selected = (selected - 1 + items.Count) % items.Count; break;
                    case ConsoleKey.DownArrow: selected = (selected + 1) % items.Count; break;
                    case ConsoleKey.Home: selected = 0; break;
                    case ConsoleKey.End: selected = items.Count - 1; break;
                    case ConsoleKey.Enter: return selected;
                    case ConsoleKey.Escape: return -1;
                    default:
                        if (key.KeyChar is >= '1' and <= '9' && key.KeyChar - '1' < items.Count) selected = key.KeyChar - '1';
                        break;
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= restore;
            ShowCursor(cursorVisible);
        }
    }

    private static void ShowCursor(bool visible)
    {
        try { Console.CursorVisible = visible; } catch (IOException) { } catch (PlatformNotSupportedException) { }
    }

    private static int Width()
    {
        try { return Math.Max(40, Console.WindowWidth - 1); }
        catch (IOException) { return 100; }
    }
}
