using NaviDnD.Data;
using NaviDnD.Data.Models;
using NaviDnD.Display;
using NaviDnD.Helpers;

namespace NaviDnD;

internal static class MouseUiHelper
{
    internal readonly record struct TitleTab(int StartX, int EndX, string Key);

    internal static void HandleDialogArrowClick((short x, short y)? clickPos, DialogDisplay? dialog)
    {
        if (clickPos == null || dialog == null) return;
        var ar = dialog.ArrowPositions;
        if (clickPos.Value.y != ar.titleY) return;
        if (clickPos.Value.x == ar.upX && ar.canScrollUp)
        {
            dialog.TryScrollUp();
            dialog.SetArrowHover(dialog.ArrowPositions.canScrollUp, false);
            Sound.PlayClick();
        }
        else if (clickPos.Value.x == ar.downX && ar.canScrollDown)
        {
            dialog.TryScrollDown();
            dialog.SetArrowHover(false, dialog.ArrowPositions.canScrollDown);
            Sound.PlayClick();
        }
    }

    // Mouse wheel over the dialog window (below its title) scrolls the history line by line: away from you toward
    // earlier lines, toward you toward newer ones. true means the wheel went to the dialog.
    internal static bool HandleDialogWheel(DialogDisplay? dialog, int notches, (short x, short y) pos)
    {
        if (dialog == null || notches == 0 || dialog.ArrowPositions.titleY <= 0 || pos.y <= dialog.ArrowPositions.titleY) return false;   // the dialog isn't drawn yet: not its wheel
        dialog.ScrollLines(notches * 2);   // 2 lines per wheel notch
        return true;
    }

    // Wheel movement accumulated since the last poll goes to the dialog if it was over it (screens without their own wheel).
    internal static void HandleDialogWheel(DialogDisplay? dialog)
    {
        int notches = ConsoleMouseReader.TakeWheel(out var pos);
        HandleDialogWheel(dialog, notches, pos);
    }

    internal static (bool isOnUp, bool isOnDown) DialogArrowHover(
        (short x, short y) mousePos, DialogDisplay? dialog)
    {
        if (dialog == null) return (false, false);
        var ar = dialog.ArrowPositions;
        if (mousePos.y != ar.titleY) return (false, false);
        return (mousePos.x == ar.upX && ar.canScrollUp,
                mousePos.x == ar.downX && ar.canScrollDown);
    }

    internal static (bool isOnLeft, bool isOnRight) SubTabArrowHover(
        (short x, short y) mousePos, HeroDisplay? hero)
    {
        if (hero == null) return (false, false);
        var si = hero.SubTabInfo;
        if (si.titleY < 0 || mousePos.y != si.titleY) return (false, false);
        return (mousePos.x == si.leftX && si.canScrollLeft,
                mousePos.x == si.rightX && si.canScrollRight);
    }

    // disabled=true dims the "[Fx]" segments (with the same ColorHelper.Darker as the other
    // inactive arrows in the project): visually shows that the buttons can't be clicked right now
    // (for example while a die is rolling and input is deliberately ignored).
    // Width of the title row (inside the frame), for "[Esc]…" at the right edge (BorderDrawer sets it on creation).
    internal static int TitleWidth;

    // "[Esc]MENU" / "[Esc]BACK" always sit at the right edge of the title on every screen. The same layout is used both for
    // drawing and for the hover/click zones (ComputeTitleTabs, SetTabHighlight).
    internal static string AlignTitle(string title)
    {
        if (TitleWidth < WindowControls.Buttons.Length + 3) return title;
        if (title.EndsWith(WindowControls.Buttons, StringComparison.Ordinal)) return title;
        int available = TitleWidth - WindowControls.Buttons.Length;
        int e = title.LastIndexOf("[Esc]", StringComparison.Ordinal);
        if (e < 0) return title.TrimEnd().PadRight(available)[..available] + WindowControls.Buttons;
        string token = title[e..].TrimEnd();
        string left = title[..e].TrimEnd();
        int gap = available - 1 - left.Length - token.Length;
        string aligned = gap < 3 ? title.PadRight(available)[..available] : left + new string(' ', gap) + token + " ";
        return aligned + WindowControls.Buttons;
    }

    internal static void WriteColoredTitle(string title, DisplayConfig display, bool disabled = false)
    {
        title = AlignTitle(title);
        var bright = ColorHelper.Pale(display.MainForeground, 0.75);
        var dim = disabled ? ColorHelper.Darker(display.MainForeground, 0.5) : KeyHintColor(display);
        int aStart = title.IndexOf('→');
        int aEnd   = aStart >= 0 ? title.IndexOf('←', aStart) : -1;

        // Window name (" MY GAMES"…, up to the first triple space) in bright text, no backdrop.
        int i = 0;
        int nameEnd = title.IndexOf("   ", StringComparison.Ordinal);
        if (title.StartsWith(' ') && nameEnd > 1 && (aStart < 0 || nameEnd < aStart))
        {
            ColorHelper.WriteColored(title[..(nameEnd + 1)], bright);
            i = nameEnd + 1;
        }
        // New game steps (ScreenRegistry.NewGameTitle): dimmed, the current «…» bright (the guillemets drawn as spaces).
        string lastStep = L.T("ADVENTURE");
        int stepsStart = title.IndexOf("1 " + L.T("WORLD"), StringComparison.Ordinal);
        int stepsEnd = stepsStart >= 0 ? title.IndexOf(lastStep, stepsStart, StringComparison.Ordinal) : -1;
        if (stepsEnd >= 0) stepsEnd += lastStep.Length + (stepsEnd + lastStep.Length < title.Length && title[stepsEnd + lastStep.Length] == '»' ? 1 : 0);
        if (stepsStart > 0 && title[stepsStart - 1] == '«') stepsStart--;
        while (i < title.Length)
        {
            if (title.EndsWith(WindowControls.Buttons, StringComparison.Ordinal)
                && i == title.Length - WindowControls.Buttons.Length)
            {
                WindowControls.Draw(display);
                break;
            }
            if (stepsEnd > 0 && i == stepsStart)
            {
                var stepDim = ColorHelper.Darker(display.MainForeground, 0.45);
                bool cur = false;
                for (; i < stepsEnd; i++)
                {
                    if (title[i] == '«') cur = true;
                    ColorHelper.WriteColored(title[i] is '«' or '»' ? " " : title[i].ToString(), cur ? bright : stepDim);
                    if (title[i] == '»') cur = false;
                }
                continue;
            }
            if (aStart >= 0 && aEnd > aStart && i == aStart)
            {
                ColorHelper.WriteColored(title[aStart..(aEnd + 1)], bright);
                i = aEnd + 1;
            }
            // An unavailable tab "{F1}NAME": the key hint as usual "[F1]", the name dark (nearly background).
            else if (title[i] == '{' && title.IndexOf('}', i) is int ce and > 0)
            {
                WriteKeyHint("[" + title[(i + 1)..ce] + "]", display);
                int ne = title.IndexOf(' ', ce);
                if (ne < 0) ne = title.Length;
                ColorHelper.WriteColored(title[(ce + 1)..ne], DisabledTabColor(display));
                i = ne;
            }
            // "›" between new game steps in the tab name color; before an unavailable step "{Fn}" it's dark, like the step.
            else if (title[i] == '›')
            {
                int next = i + 1;
                while (next < title.Length && title[next] == ' ') next++;
                ColorHelper.WriteColored("›", next < title.Length && title[next] == '{' ? DisabledTabColor(display) : display.MainForeground);
                i++;
            }
            else if (title[i] == '[')
            {
                int end = title.IndexOf(']', i);
                if (end >= 0)
                {
                    if (disabled) ColorHelper.WriteColored(title[i..(end + 1)], dim);
                    else WriteKeyHint(title[i..(end + 1)], display);
                    i = end + 1;
                }
                else
                {
                    Console.Write(title[i]);
                    i++;
                }
            }
            else
            {
                Console.Write(title[i]);
                i++;
            }
        }
    }

    // Key hints "[F1]", "[Esc]" are dimmed so they don't compete with tab names.
    // Key hint: dimmed, or spaces of the same length if hints are turned off in settings.
    internal static void WriteKeyHint(string hint, DisplayConfig display)
    {
        if (display.ShowKeyHints) ColorHelper.WriteColored(hint, KeyHintColor(display));
        else Console.Write(new string(' ', hint.Length));
    }

    // Frame lines: the dimmed main color (like the new-game block frames), so text over them reads better.
    internal static List<int> FrameColor(DisplayConfig display) => ColorHelper.Darker(display.MainForeground, 0.5);

    // Unavailable tab/step: nearly background.
    internal static List<int> DisabledTabColor(DisplayConfig display) => ColorHelper.MixWith(display.MainForeground, display.MainBackground, 0.68);

    internal static List<int> KeyHintColor(DisplayConfig display) => ColorHelper.MixWith(display.MainForeground, display.MainBackground, 0.9);

    internal static List<TitleTab> ComputeTitleTabs(string title)
    {
        title = AlignTitle(title);
        var tabs = new List<TitleTab>();
        foreach (var (prefix, key) in new (string, string)[] { ("[F1]","F1"), ("[F2]","F2"), ("[F3]","F3"), ("[F4]","F4"), ("[F5]","F5"), ("[Esc]","Esc") })
        {
            int idx = title.IndexOf(prefix);
            if (idx < 0) continue;
            int end = idx + prefix.Length;
            while (end < title.Length && title[end] != ' ') end++;
            // Left margin + frame(1) up to the start of the title text. The hover/click zone is the name only:
            // the key hint "[F1]" isn't highlighted and the cursor doesn't change over it.
            int titleStart = DisplayConfig.LeftMargin + 1;
            tabs.Add(new TitleTab(titleStart + idx + prefix.Length, titleStart + end, key));
        }
        return tabs;
    }

    internal static string? GetHoveredTabKey(short mouseX, short mouseY, List<TitleTab> tabs)
    {
        if (mouseY != 1) return null;
        foreach (var tab in tabs)
            if (mouseX >= tab.StartX && mouseX < tab.EndX)
                return tab.Key;
        return null;
    }

    internal static void SetTabHighlight(string title, List<TitleTab> tabs, string key, bool highlighted, DisplayConfig display)
    {
        var tab = tabs.Find(t => t.Key == key);
        if (tab.Key == null) return;
        title = AlignTitle(title);
        bool vis = Console.CursorVisible;
        Console.CursorVisible = false;
        int sl = Console.CursorLeft, st = Console.CursorTop;
        Console.SetCursorPosition(tab.StartX, 1);
        // tab.StartX/EndX are screen columns (already including the margin); title is the original string without
        // the margin, so to index into title it must be subtracted back.
        string tabText = title.Substring(tab.StartX - 1 - DisplayConfig.LeftMargin, tab.EndX - tab.StartX);
        var fg = highlighted ? ColorHelper.Pale(display.MainForeground, 0.75) : display.MainForeground;
        int keyEnd = tabText.StartsWith('[') ? tabText.IndexOf(']') + 1 : 0;
        if (keyEnd > 0)
            ColorHelper.WriteColored(tabText[..keyEnd], fgColor: KeyHintColor(display), bgColor: display.MainBackground); // the key isn't highlighted
        ColorHelper.WriteColored(tabText[keyEnd..], fgColor: fg, bgColor: display.MainBackground);
        Console.SetCursorPosition(sl, st);
        Console.CursorVisible = vis;
    }

    internal static Action MakeCharacterPollAction(
        DialogDisplay dialog, HeroDisplay heroDisplay,
        int drawStartTop, Action draw,
        Action? onScrollLeft, Action? onScrollRight,
        string title, List<TitleTab> titleTabs, DisplayConfig display,
        Action<int, int>? onInventorySelect = null,
        Action<int, int>? onSpellSelect = null)
    {
        string? hoveredTabKey = null;
        string? hoveredSubTabLabel = null; // "inv", "abil", or null
        int hoveredInvItem = -1;
        int hoveredSpellItem = -1;

        // x < splitX[0] → column 0, x < splitX[1] → column 1, x < splitX[2] → column 2, otherwise 3.
        static int SpellColumnIndex(short x, int[] splitX)
        {
            if (x < splitX[0]) return 0;
            if (x < splitX[1]) return 1;
            if (x < splitX[2]) return 2;
            return 3;
        }

        void RedrawAll()
        {
            int savedLeft = Console.CursorLeft, savedTop = Console.CursorTop;
            Console.CursorVisible = false;
            heroDisplay.RefreshSelection();
            dialog.RerenderRightPanel();
            Console.SetCursorPosition(savedLeft, savedTop);
        }

        return () =>
        {
            if (ApplyPreparedPicture(display)) dialog.RerenderRightPanel();
            var activeTitle = display.StreamingTabTitle ?? title;
            var activeTabs  = display.StreamingTabTitle != null ? ComputeTitleTabs(activeTitle) : titleTabs;

            var (mousePos, clickPos, clickCount) = ConsoleMouseReader.DrainMouseEvents();

            // A truncated card value under the mouse scrolls as a marquee (it ticks even without mouse movement).
            heroDisplay?.UpdateMarquee(mousePos);

            HandleDialogArrowClick(clickPos, dialog);
            HandleDialogWheel(dialog);

            if (clickPos.HasValue)
            {
                var clickedTabKey = GetHoveredTabKey(clickPos.Value.x, clickPos.Value.y, activeTabs);
                if (clickedTabKey != null) { Sound.PlayClick(); display.PendingCommand = clickedTabKey; return; }
                if (clickPos.Value.y is >= 0 and <= 2) { ConsoleMouseReader.StartWindowDrag(); return; }

                // The "Level up" button in the card's level row does the same as F10.
                if (heroDisplay.IsOnLevelUp(clickPos.Value)) { Sound.PlayClick(); display.PendingCommand = "F10"; return; }

                var si = heroDisplay.SubTabInfo;
                if (si.titleY >= 0 && clickPos.Value.y == si.titleY)
                {
                    // Sub-tab label clicks → switch sub-tab (sound only if it actually switches:
                    // clicking an ALREADY active tab changes nothing, just like an unavailable arrow)
                    if (clickPos.Value.x >= si.invStartX && clickPos.Value.x < si.invEndX)
                        { if (display.CharacterSubTab != CharacterSubTab.Inventory) Sound.PlayClick(); display.PendingCommand = "F6"; return; }
                    if (clickPos.Value.x >= si.abilStartX && clickPos.Value.x < si.abilEndX)
                        { if (display.CharacterSubTab != CharacterSubTab.Abilities) Sound.PlayClick(); display.PendingCommand = "F7"; return; }
                    if (clickPos.Value.x >= si.effStartX && clickPos.Value.x < si.effEndX)
                        { if (display.CharacterSubTab != CharacterSubTab.Effects) Sound.PlayClick(); display.PendingCommand = "F8"; return; }
                    if (clickPos.Value.x >= si.spellStartX && clickPos.Value.x < si.spellEndX)
                        { if (display.CharacterSubTab != CharacterSubTab.Spells) Sound.PlayClick(); display.PendingCommand = "F9"; return; }

                    bool handled = false;
                    if (clickPos.Value.x == si.leftX && si.canScrollLeft && onScrollLeft != null)
                    {
                        heroDisplay.SetSubTabArrowHover(false, false);
                        onScrollLeft();
                        handled = true;
                    }
                    else if (clickPos.Value.x == si.rightX && si.canScrollRight && onScrollRight != null)
                    {
                        heroDisplay.SetSubTabArrowHover(false, false);
                        onScrollRight();
                        handled = true;
                    }
                    if (handled)
                    {
                        Sound.PlayClick();
                        int savedLeft = Console.CursorLeft, savedTop = Console.CursorTop;
                        Console.CursorVisible = false;
                        Console.SetCursorPosition(0, drawStartTop);
                        draw();
                        Console.SetCursorPosition(savedLeft, savedTop);
                        if (mousePos != null)
                        {
                            var (isL, isR) = SubTabArrowHover(mousePos.Value, heroDisplay);
                            heroDisplay.SetSubTabArrowHover(isL, isR);
                            ConsoleMouseReader.SetCursorShape(isL || isR);
                        }
                        return;
                    }
                }

                // Click on the note page arrows (◄ N/M ►) in the image panel. Not a return: the same
                // DrainMouseEvents() sometimes yields clickPos TOGETHER with a fresh mousePos (the click and
                // the next mouse movement both get queued before a single poll tick); an early
                // return here silently dropped this tick's mousePos and hover stopped being tracked
                // until the next click.
                bool clickHandled = false;
                var na = dialog.NotePageArrowPositions;
                if (na.arrowY >= 0 && clickPos.Value.y == na.arrowY)
                {
                    bool isSpellTab = display.CharacterSubTab == CharacterSubTab.Spells;
                    int effectiveIdx = isSpellTab
                        ? (hoveredSpellItem >= 0 ? hoveredSpellItem : display.PinnedSpellIndex)
                        : (hoveredInvItem >= 0 ? hoveredInvItem : display.PinnedInventoryIndex);
                    var select = isSpellTab ? onSpellSelect : onInventorySelect;
                    if (effectiveIdx >= 0 && select != null)
                    {
                        // clickCount: rapid repeated clicks on an arrow can queue up
                        // between poll ticks; DrainMouseEvents() yields only the last click position
                        // but counts all presses, otherwise some clicks would be silently lost (paging by 1
                        // instead of N on a fast click).
                        int steps = Math.Max(1, clickCount);
                        if (clickPos.Value.x == na.leftX)
                        {
                            if (display.NotePage > 0) Sound.PlayClick();
                            select(effectiveIdx, display.NotePage - steps);
                            RedrawAll();
                            clickHandled = true;
                        }
                        else if (clickPos.Value.x == na.rightX)
                        {
                            if (display.NotePage < display.NotePageCount - 1) Sound.PlayClick();
                            select(effectiveIdx, display.NotePage + steps);
                            RedrawAll();
                            clickHandled = true;
                        }
                    }
                }

                // Clicking an inventory row pins the item (yellow arrow, survives the mouse leaving)
                if (!clickHandled && display.CharacterSubTab == CharacterSubTab.Inventory && onInventorySelect != null)
                {
                    var hi = heroDisplay.InventoryHitInfo;
                    if (hi.startY >= 0 && hi.rows > 0)
                    {
                        int rowOffset = clickPos.Value.y - hi.startY;
                        if (rowOffset >= 0 && rowOffset < hi.rows)
                        {
                            int clickedIdx = clickPos.Value.x < hi.splitX
                                ? (rowOffset < hi.col1Map.Length ? hi.col1Map[rowOffset] : -1)
                                : (rowOffset < hi.col2Map.Length ? hi.col2Map[rowOffset] : -1);
                            if (clickedIdx >= 0)
                            {
                                // Clicking an already pinned item changes nothing and makes no sound.
                                if (display.PinnedInventoryIndex != clickedIdx) Sound.PlayClick();
                                display.SelectedInventoryIndex = clickedIdx;
                                display.PinnedInventoryIndex = clickedIdx;
                                hoveredInvItem = clickedIdx;
                                onInventorySelect(clickedIdx, 0);
                                RedrawAll();
                            }
                        }
                    }
                }

                // Clicking a spell row pins it (like clicking the inventory, but across 4 columns).
                if (!clickHandled && display.CharacterSubTab == CharacterSubTab.Spells && onSpellSelect != null)
                {
                    var shi = heroDisplay.SpellsHitInfo;
                    if (shi.startY >= 0 && shi.rows > 0)
                    {
                        int rowOffset = clickPos.Value.y - shi.startY;
                        if (rowOffset >= 0 && rowOffset < shi.rows)
                        {
                            int col = SpellColumnIndex(clickPos.Value.x, shi.splitX);
                            int clickedIdx = rowOffset < shi.colMaps[col].Length ? shi.colMaps[col][rowOffset] : -1;
                            if (clickedIdx >= 0)
                            {
                                if (display.PinnedSpellIndex != clickedIdx) Sound.PlayClick();
                                display.SelectedSpellIndex = clickedIdx;
                                display.PinnedSpellIndex = clickedIdx;
                                hoveredSpellItem = clickedIdx;
                                onSpellSelect(clickedIdx, 0);
                                RedrawAll();
                            }
                        }
                    }
                }
            }

            if (mousePos == null) return;

            var newTabKey = GetHoveredTabKey(mousePos.Value.x, mousePos.Value.y, activeTabs);
            if (newTabKey != hoveredTabKey)
            {
                if (hoveredTabKey != null) SetTabHighlight(activeTitle, activeTabs, hoveredTabKey, false, display);
                hoveredTabKey = newTabKey;
                if (hoveredTabKey != null) SetTabHighlight(activeTitle, activeTabs, hoveredTabKey, true, display);
            }
            if (hoveredTabKey != null) { heroDisplay.SetLevelUpHover(false); ConsoleMouseReader.SetCursorShape(true); return; }

            // Sub-tab label hover → highlight + hand cursor
            var subInfo = heroDisplay.SubTabInfo;
            string? newHoveredSubTab = null;
            if (subInfo.titleY >= 0 && mousePos.Value.y == subInfo.titleY)
            {
                if (mousePos.Value.x >= subInfo.invStartX   && mousePos.Value.x < subInfo.invEndX)   newHoveredSubTab = "inv";
                if (mousePos.Value.x >= subInfo.abilStartX  && mousePos.Value.x < subInfo.abilEndX)  newHoveredSubTab = "abil";
                if (mousePos.Value.x >= subInfo.effStartX   && mousePos.Value.x < subInfo.effEndX)   newHoveredSubTab = "eff";
                if (mousePos.Value.x >= subInfo.spellStartX && mousePos.Value.x < subInfo.spellEndX) newHoveredSubTab = "spell";
            }
            if (newHoveredSubTab != hoveredSubTabLabel)
            {
                hoveredSubTabLabel = newHoveredSubTab;
                heroDisplay.SetSubTabLabelHover(hoveredSubTabLabel);
            }

            // Inventory item hover
            int newHoveredInv = -1;
            if (display.CharacterSubTab == CharacterSubTab.Inventory)
            {
                var hi = heroDisplay.InventoryHitInfo;
                if (hi.startY >= 0 && hi.rows > 0)
                {
                    int rowOffset = mousePos.Value.y - hi.startY;
                    if (rowOffset >= 0 && rowOffset < hi.rows)
                        newHoveredInv = mousePos.Value.x < hi.splitX
                            ? (rowOffset < hi.col1Map.Length ? hi.col1Map[rowOffset] : -1)
                            : (rowOffset < hi.col2Map.Length ? hi.col2Map[rowOffset] : -1);
                }
            }
            if (newHoveredInv != hoveredInvItem)
            {
                bool needRedraw = false;
                if (newHoveredInv >= 0)
                {
                    display.SelectedInventoryIndex = newHoveredInv;
                    onInventorySelect?.Invoke(newHoveredInv, 0);
                    needRedraw = true;
                }
                else if (hoveredInvItem >= 0)
                {
                    if (display.PinnedInventoryIndex >= 0)
                    {
                        display.SelectedInventoryIndex = display.PinnedInventoryIndex;
                        onInventorySelect?.Invoke(display.PinnedInventoryIndex, 0);
                    }
                    else
                    {
                        CancelPreparedPicture(display);
                        display.SelectedInventoryIndex = -1;
                        display.SelectedImageLines = null;
                        display.SelectedImageColor = null;
                        display.NotePage = 0;
                        display.NotePageCount = 0;
                    }
                    needRedraw = true;
                }
                hoveredInvItem = newHoveredInv;
                if (needRedraw) RedrawAll();
            }

            // Spell item hover (like the inventory, but across 4 columns)
            int newHoveredSpell = -1;
            if (display.CharacterSubTab == CharacterSubTab.Spells)
            {
                var si = heroDisplay.SpellsHitInfo;
                if (si.startY >= 0 && si.rows > 0)
                {
                    int rowOffset = mousePos.Value.y - si.startY;
                    if (rowOffset >= 0 && rowOffset < si.rows)
                    {
                        int col = SpellColumnIndex(mousePos.Value.x, si.splitX);
                        newHoveredSpell = rowOffset < si.colMaps[col].Length ? si.colMaps[col][rowOffset] : -1;
                    }
                }
            }
            if (newHoveredSpell != hoveredSpellItem)
            {
                bool needRedrawSpell = false;
                if (newHoveredSpell >= 0)
                {
                    display.SelectedSpellIndex = newHoveredSpell;
                    onSpellSelect?.Invoke(newHoveredSpell, 0);
                    needRedrawSpell = true;
                }
                else if (hoveredSpellItem >= 0)
                {
                    if (display.PinnedSpellIndex >= 0)
                    {
                        display.SelectedSpellIndex = display.PinnedSpellIndex;
                        onSpellSelect?.Invoke(display.PinnedSpellIndex, 0);
                    }
                    else
                    {
                        display.SelectedSpellIndex = -1;
                        display.SelectedImageLines = null;
                        display.SelectedImageColor = null;
                        display.NotePage = 0;
                        display.NotePageCount = 0;
                    }
                    needRedrawSpell = true;
                }
                hoveredSpellItem = newHoveredSpell;
                if (needRedrawSpell) RedrawAll();
            }

            var (isOnUp, isOnDown) = DialogArrowHover(mousePos.Value, dialog);
            var (isOnLeft, isOnRight) = SubTabArrowHover(mousePos.Value, heroDisplay);
            var (isOnNoteLeft, isOnNoteRight) = NotePageArrowHover(mousePos.Value, dialog, display);
            bool isOnAny = isOnUp || isOnDown || isOnLeft || isOnRight || isOnNoteLeft || isOnNoteRight
                           || newHoveredSubTab != null || newHoveredInv >= 0 || newHoveredSpell >= 0
                           || heroDisplay.IsOnLevelUp(mousePos.Value);
            ConsoleMouseReader.SetCursorShape(isOnAny);
            dialog.SetArrowHover(isOnUp, isOnDown);
            heroDisplay.SetSubTabArrowHover(isOnLeft, isOnRight);
            heroDisplay.SetLevelUpHover(heroDisplay.IsOnLevelUp(mousePos.Value));
            dialog.SetNoteArrowHover(isOnNoteLeft, isOnNoteRight);
        };
    }

    internal static (bool isOnLeft, bool isOnRight) NotePageArrowHover(
        (short x, short y) mousePos, DialogDisplay? dialog, DisplayConfig display)
    {
        if (dialog == null) return (false, false);
        var na = dialog.NotePageArrowPositions;
        if (na.arrowY < 0 || mousePos.y != na.arrowY) return (false, false);
        // An unavailable (dimmed) arrow shouldn't give the hand cursor, just like the other
        // disabled arrows in the project.
        bool onLeft  = mousePos.x == na.leftX  && display.NotePage > 0;
        bool onRight = mousePos.x == na.rightX && display.NotePage < display.NotePageCount - 1;
        return (onLeft, onRight);
    }

    // Journal: title tabs and dialog arrows as on the other screens, plus the mouse over the book
    // (bookmarks, ◄ ►, hovering an entry → details in the image block).
    internal static Action MakeJournalPollAction(DialogDisplay dialog, NaviDnD.Display.JournalDisplay journal, string title, DisplayConfig display)
    {
        var titleTabs = ComputeTitleTabs(title);
        string? hoveredTabKey = null;
        return () =>
        {
            if (ApplyPreparedPicture(display)) dialog.RerenderRightPanel();
            var activeTitle = display.StreamingTabTitle ?? title;
            var activeTabs  = display.StreamingTabTitle != null ? ComputeTitleTabs(activeTitle) : titleTabs;
            var (mousePos, clickPos, _) = ConsoleMouseReader.DrainMouseEvents();
            HandleDialogArrowClick(clickPos, dialog);
            HandleDialogWheel(dialog);
            if (clickPos.HasValue)
            {
                var clickedTabKey = GetHoveredTabKey(clickPos.Value.x, clickPos.Value.y, activeTabs);
                if (clickedTabKey != null) { Sound.PlayClick(); display.PendingCommand = clickedTabKey; return; }
                if (clickPos.Value.y is >= 0 and <= 2) { ConsoleMouseReader.StartWindowDrag(); return; }
            }
            if (journal.HandleMouse(mousePos, clickPos)) dialog.RerenderRightPanel();
            if (mousePos == null) return;
            var newTabKey = GetHoveredTabKey(mousePos.Value.x, mousePos.Value.y, activeTabs);
            if (newTabKey != hoveredTabKey)
            {
                if (hoveredTabKey != null) SetTabHighlight(activeTitle, activeTabs, hoveredTabKey, false, display);
                hoveredTabKey = newTabKey;
                if (hoveredTabKey != null) SetTabHighlight(activeTitle, activeTabs, hoveredTabKey, true, display);
            }
            if (hoveredTabKey != null) { ConsoleMouseReader.SetCursorShape(true); return; }
            var (isOnUp, isOnDown) = DialogArrowHover(mousePos.Value, dialog);
            if (isOnUp || isOnDown) ConsoleMouseReader.SetCursorShape(true);
            dialog.SetArrowHover(isOnUp, isOnDown);
        };
    }

    internal static Action MakeDialogArrowPollAction(DialogDisplay dialog, string title, List<TitleTab> titleTabs, DisplayConfig display)
    {
        string? hoveredTabKey = null;
        return () =>
        {
            if (ApplyPreparedPicture(display)) dialog.RerenderRightPanel();
            var activeTitle = display.StreamingTabTitle ?? title;
            var activeTabs  = display.StreamingTabTitle != null ? ComputeTitleTabs(activeTitle) : titleTabs;

            var (mousePos, clickPos, _) = ConsoleMouseReader.DrainMouseEvents();
            HandleDialogArrowClick(clickPos, dialog);
            HandleDialogWheel(dialog);
            if (clickPos.HasValue)
            {
                var clickedTabKey = GetHoveredTabKey(clickPos.Value.x, clickPos.Value.y, activeTabs);
                if (clickedTabKey != null) { Sound.PlayClick(); display.PendingCommand = clickedTabKey; return; }
                if (clickPos.Value.y is >= 0 and <= 2) { ConsoleMouseReader.StartWindowDrag(); return; }
            }
            if (mousePos == null) return;
            var newTabKey = GetHoveredTabKey(mousePos.Value.x, mousePos.Value.y, activeTabs);
            if (newTabKey != hoveredTabKey)
            {
                if (hoveredTabKey != null) SetTabHighlight(activeTitle, activeTabs, hoveredTabKey, false, display);
                hoveredTabKey = newTabKey;
                if (hoveredTabKey != null) SetTabHighlight(activeTitle, activeTabs, hoveredTabKey, true, display);
            }
            if (hoveredTabKey != null) { ConsoleMouseReader.SetCursorShape(true); return; }
            var (isOnUp, isOnDown) = DialogArrowHover(mousePos.Value, dialog);
            ConsoleMouseReader.SetCursorShape(isOnUp || isOnDown);
            dialog.SetArrowHover(isOnUp, isOnDown);
        };
    }

    // d20 placeholder with no picture: the die is centered in the area ABOVE the name, the name sits on a fixed
    // second-to-last row (matching where the footer ends for a placeholder if there
    // were a picture). Previously the die was right at the start of the array (row 0): the short placeholder
    // visually "stuck" to the top of the panel instead of being centered above the name.
    private static List<string> BuildCenteredArtWithName(IReadOnlyList<string> art, string name, int totalLines)
    {
        int end = art.Count;
        while (end > 0 && string.IsNullOrEmpty(art[end - 1])) end--;

        var lines = new List<string>();
        // The center is computed from the WHOLE panel height (totalLines), not just the area above the name;
        // otherwise the name + blank line at the bottom "pull" the die's center 1 row above the true
        // middle of the panel.
        int artStart = Math.Max(0, (totalLines - end) / 2);
        for (int i = 0; i < artStart; i++) lines.Add("");
        for (int i = 0; i < end; i++) lines.Add(art[i]);
        while (lines.Count < totalLines - 2) lines.Add("");
        lines.Add(name);
        return lines;
    }

    // No picture (not set or file not found): a d20 placeholder instead of an empty panel; otherwise
    // BuildRightPanelLines would silently fall through to DefaultImageLines (the HERO's portrait), which would look
    // as if the hovered item had the hero's picture rather than "no picture".
    private static string[] BuildDefaultIconLines(string? name, int totalLines)
    {
        if (string.IsNullOrEmpty(name)) return DiceArt.D20.Split('\n');
        var lines = BuildCenteredArtWithName(DiceArt.D20.Split('\n'), name, totalLines);
        lines.Add(""); // the last row is blank: there's no page indicator here
        return lines.ToArray();
    }

    // The single entry point for selecting a map element (mouse/Tab in MapScreenLoop): a known monster kind
    // (narrative.knownMonsters contains its monsterKey) shows the stat card; otherwise, as
    // before, just picture+name. The hero/objects/unidentified creatures don't qualify
    // (GetSelectedLivingEntity returns null for objects, MonsterKey may be unset).
    // page is the monster card page (paging ◄ ► on the map, MapScreenLoop).
    internal static void SetSelectedMapEntity(DisplayConfig display, LegendDisplay legendDisplay, WorldState settings, int page = 0)
    {
        var entity = legendDisplay.GetSelectedLivingEntity();
        bool known = entity?.MonsterKey != null
            && settings.Narrative?.KnownMonsters?.Contains(entity.MonsterKey, StringComparer.OrdinalIgnoreCase) == true;
        if (entity != null && known)
        {
            SetSelectedMonster(display, entity, MonsterDatabase.Find(entity.MonsterKey), page);
            return;
        }

        var (imgPath, imgName, imgColor) = legendDisplay.GetSelectedEntityImageAndName();
        SetSelectedImage(display, imgPath, imgName, imgColor);
        display.NotePage = 0;
        display.NotePageCount = 0;
    }

    // Door picture: its own (the AI sets one only for special doors) or a fixed one by state and the terrain type
    // of the block where the door is (the model doesn't look up ordinary doors via find_icon).
    internal static string DoorImage(NaviDnD.Data.Models.Door door, NaviDnD.Data.Models.MapConfig map)
    {
        if (!string.IsNullOrEmpty(door.Image)) return door.Image;
        string? theme = door.From is { Count: >= 2 } f ? map.ChunkAt(f[0], f[1])?.Theme : null;
        bool open = door.IsDoor != true || door.IsDoorOpen == true;
        if (door.IsWindow == true) return open ? "delapouite/window" : "delapouite/window-bars";
        if (door.IsWorldExit == true)
            return theme switch
            {
                "building" => "delapouite/house",
                "village" => "delapouite/village",
                "dungeon" => "delapouite/crypt-entrance",
                "cave" => "delapouite/cave-entrance",
                "forest" => "delapouite/forest-entrance",
                "hills" or "mountain" => "delapouite/mountain-road",
                "plains" or "swamp" => "delapouite/horizon-road",
                _ => "delapouite/exit-door",
            };
        if (NaviDnD.Data.Models.MapChunk.IsOutdoorTheme(theme))
            return open ? "delapouite/open-gate" : "delapouite/gate";
        if (theme == NaviDnD.Data.Models.MapChunk.Cave)
            return open ? "delapouite/underground-cave" : "delapouite/door";
        return open ? "lorc/doorway" : "delapouite/door";
    }

    // The caption under the picture is one line: a long name didn't fit the panel width and broke the frame.
    private static string? FitPanelName(DisplayConfig display, string? name)
    {
        int width = Math.Max(4, display.DialogRightPanelWidth - 2);
        if (name == null || name.Length <= width) return name;
        return name[..(width - 1)].TrimEnd() + "…";
    }

    internal static void SetSelectedImage(DisplayConfig display, string? imagePath, string? name, List<int>? color = null)
    {
        name = FitPanelName(display, name);
        display.SelectedImageLines = string.IsNullOrEmpty(imagePath)
            ? BuildDefaultIconLines(name, display.MaxHistoryLines)
            : SvgToBrailleConverter.Convert(imagePath, name ?? "") ?? BuildDefaultIconLines(name, display.MaxHistoryLines);
        display.SelectedImageColor = color;
        display.SelectedImageColorTitleOnly = false;
    }

    internal static void SetDefaultImage(DisplayConfig display, string? imagePath, List<int>? color = null)
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            display.DefaultImageLines = null;
            display.DefaultImageColor = null;
            return;
        }

        display.DefaultImageLines = SvgToBrailleConverter.Convert(imagePath, "");
        display.DefaultImageColor = color;
    }

    // An item with readable text (item.Text): page 0 is picture+name (like a normal item),
    // pages 1..N are the text itself, cut into pages to fit the panel height. Without text it's the same
    // SetSelectedImage as before, NotePageCount=0 (no paging arrows).
    private sealed class PendingPicture
    {
        public Task? Ready;
        public Action? Apply;
        public string[]? Previous;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DisplayConfig, PendingPicture> PendingPictures = new();

    internal static bool PreparePicture(DisplayConfig display, string? image, Action apply)
    {
        var pending = PendingPictures.GetOrCreateValue(display);
        pending.Apply = null;
        var ready = SvgToBrailleConverter.PrepareAsync(image);
        if (ready.IsCompleted) return true;
        pending.Ready = ready;
        pending.Previous = display.SelectedImageLines;
        pending.Apply = apply;
        return false;
    }

    internal static bool ApplyPreparedPicture(DisplayConfig display)
    {
        if (!PendingPictures.TryGetValue(display, out var pending) || pending.Ready?.IsCompleted != true || pending.Apply == null) return false;
        var apply = pending.Apply;
        pending.Apply = null;
        if (!ReferenceEquals(pending.Previous, display.SelectedImageLines)) return false;
        apply();
        return true;
    }

    internal static void CancelPreparedPicture(DisplayConfig display)
    {
        if (PendingPictures.TryGetValue(display, out var pending)) pending.Apply = null;
    }

    internal static void SetSelectedInventoryItem(DisplayConfig display, HeroInventory item, int page)
    {
        if (!PreparePicture(display, item.Image, () => SetSelectedInventoryItem(display, item, page))) return;
        if (string.IsNullOrEmpty(item.Text))
        {
            SetSelectedImage(display, item.Image, item.Name, item.Color);
            display.NotePage = 0;
            display.NotePageCount = 0;
            return;
        }

        int total = display.MaxHistoryLines;
        // 1-character padding from the panel frame on the left and right: PadRight keeps all text lines the same
        // length, so the centering leftPad in BuildRightPanelLines is identical for all of them (not
        // "the actual center of each line" but a straight left edge with margins).
        int textWidth = Math.Max(1, display.DialogRightPanelWidth - 2);
        // The name and page indicator are always at the bottom (as on the picture page); the text starts
        // on the first row: text area + blank + name + indicator.
        int textAreaLines = Math.Max(1, total - 3);
        var textPages = BuildNotePages(item.Text, textWidth, textAreaLines);
        int pageCount = 1 + textPages.Count;
        page = Math.Clamp(page, 0, pageCount - 1);

        List<string> lines;
        if (page == 0)
        {
            // The name is always pinned to the panel's second-to-last row: the picture's own height
            // (18 rows for a real icon, 9 for the d20 placeholder) must not affect it, otherwise for
            // a small picture the name/indicator would float higher than intended.
            var realArt = string.IsNullOrEmpty(item.Image) ? null : SvgToBrailleConverter.Convert(item.Image, "");
            if (realArt != null)
            {
                // A real icon takes nearly the whole panel height, so no top padding is needed;
                // centering is noticeable only for the short d20 placeholder (see BuildCenteredArtWithName).
                lines = [.. realArt];
                while (lines.Count < total - 2) lines.Add("");
                lines.Add(FitPanelName(display, item.Name) ?? "");
            }
            else
            {
                lines = BuildCenteredArtWithName(DiceArt.D20.Split('\n'), item.Name, total);
            }
        }
        else
        {
            lines = [];
            foreach (var l in textPages[page - 1]) lines.Add(l.PadRight(textWidth));
            while (lines.Count < textAreaLines) lines.Add("");
            lines.Add("");
            lines.Add(FitPanelName(display, item.Name) ?? "");
        }
        lines.Add(DialogDisplay.NotePageIndicatorText(page, pageCount));

        display.SelectedImageLines = lines.ToArray();
        display.SelectedImageColor = item.Color;
        display.SelectedImageColorTitleOnly = false;
        display.NotePage = page;
        display.NotePageCount = pageCount;
    }

    private static List<List<string>> BuildNotePages(string text, int width, int linesPerPage)
    {
        var wrapped = TextWrapper.WrapText(text, width);
        var pages = new List<List<string>>();
        for (int i = 0; i < wrapped.Count; i += linesPerPage)
            pages.Add(wrapped.Skip(i).Take(linesPerPage).ToList());
        if (pages.Count == 0) pages.Add([]);
        return pages;
    }

    // The monster card is laid out like a note in the inventory (SetSelectedInventoryItem): page 0
    // is picture+name, then the stats page by page. Unlike the spell card (where the header is
    // fixed and only the text pages) there's no header at all: the whole statblock is just one
    // long text cut into pages, with the picture always first.
    // info == null: the kind isn't identified (or a homebrew creature has no monsterKey): a plain picture
    // without stats, like any other entity without text.
    internal static void SetSelectedMonster(DisplayConfig display, LivingEntity entity, MonsterInfo? info, int page)
    {
        if (info == null)
        {
            SetSelectedImage(display, entity.Image, entity.Name, entity.Color);
            display.NotePage = 0;
            display.NotePageCount = 0;
            return;
        }

        int total = display.MaxHistoryLines;
        int textWidth = Math.Max(1, display.DialogRightPanelWidth - 2);
        int textAreaLines = Math.Max(1, total - 3);
        var palette = new MonsterCard.Palette(
            Dim: ColorHelper.Darker(display.MainForeground, 0.6),
            Accent: entity.Color ?? display.MainForeground,
            Good: [120, 205, 120],
            Warn: [230, 170, 80]);
        var textPages = MonsterCard.Paginate(MonsterCard.Build(info, textWidth, palette), textAreaLines);
        int pageCount = 1 + textPages.Count;
        page = Math.Clamp(page, 0, pageCount - 1);

        List<string> lines;
        List<(string, List<int>?)>?[]? segments = null;
        if (page == 0)
        {
            var realArt = string.IsNullOrEmpty(entity.Image) ? null : SvgToBrailleConverter.Convert(entity.Image, "");
            if (realArt != null)
            {
                lines = [.. realArt];
                while (lines.Count < total - 2) lines.Add("");
                lines.Add(entity.Name);
            }
            else
            {
                lines = BuildCenteredArtWithName(DiceArt.D20.Split('\n'), entity.Name, total);
            }
        }
        else
        {
            // Multicolored stat text: placeholder lines of the same width (for panel alignment) +
            // colored runs (DialogDisplay draws them instead of the placeholders). The name at the bottom is in the creature's color.
            lines = [];
            var segs = new List<List<(string, List<int>?)>?>();
            foreach (var l in textPages[page - 1])
            {
                lines.Add(l.Plain.PadRight(textWidth));
                segs.Add(l.Segments);
            }
            while (lines.Count < textAreaLines) { lines.Add(new string(' ', textWidth)); segs.Add(null); }
            lines.Add("");
            segs.Add(null);
            lines.Add(entity.Name);
            segs.Add([(entity.Name, palette.Accent)]);
            segments = [.. segs];
        }
        lines.Add(DialogDisplay.NotePageIndicatorText(page, pageCount));

        var lineArray = lines.ToArray();
        display.SelectedImageLines = lineArray;
        display.SelectedImageSegments = segments;
        display.SelectedImageSegmentsFor = segments != null ? lineArray : null;
        display.SelectedImageColor = entity.Color;
        display.SelectedImageColorTitleOnly = false;
        display.NotePage = page;
        display.NotePageCount = pageCount;
    }

    // Paragraphs → word wrap line by line, a blank line between paragraphs (like WrapSpellText, just without
    // parsing <br>: the paragraphs here are already laid out as a list).
    private static List<string> WrapParagraphs(List<string> paragraphs, int width)
    {
        var lines = new List<string>();
        foreach (var raw in paragraphs)
        {
            string trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            if (lines.Count > 0) lines.Add("");
            lines.AddRange(TextWrapper.WrapText(trimmed, width));
        }
        return lines.Count > 0 ? lines : [L.T("No data.")];
    }

    // Width of the label column in the casting time/range/components/duration block: all values
    // align to the longest label ("Duration"), like the character's stat
    // column (HeroDisplay.DrawHeroCard: key.PadRight(leftKeyMax) + separator).
    private static int SpellFieldLabelWidth =>
        new[] { L.T("Casting time"), L.T("Range"), L.T("Components"), L.T("Duration") }.Max(l => l.Length);

    // Spell card: the header (name, level+school, casting time/range/components/duration)
    // stays the same on every page and only the description text pages, unlike an
    // item with a note (there the first page is the picture and the text starts from the beginning).
    // Level/school/casting time etc. come from SpellDatabase (file GameData/DnD5e_spells_BD.dtn) by the spell
    // name; if the name isn't in the database (for example, a spell the AI invented that isn't in the reference),
    // we show the little the hero has (name, level), without the other fields.
    internal static void SetSelectedSpell(DisplayConfig display, HeroSpell spell, int page)
    {
        CancelPreparedPicture(display);
        var info = SpellDatabase.Find(spell.Name)?.Localized;
        int total = display.MaxHistoryLines;
        int textWidth = Math.Max(1, display.DialogRightPanelWidth - 2);

        string levelText = spell.Level <= 0 ? L.T("Cantrip") : L.F("level {0}", LevelToRoman(spell.Level));
        string schoolText = Capitalize(info?.School);
        string subtitle = schoolText.Length == 0 ? levelText : $"{levelText}, {schoolText}";

        var header = new List<string>
        {
            spell.Name.ToUpperInvariant(),
            subtitle,
            new string('─', textWidth),
            SpellField(L.T("Casting time"), info?.CastingTime, textWidth),
            SpellField(L.T("Range"), info?.Range, textWidth),
            SpellField(L.T("Components"), info?.Components, textWidth),
            SpellField(L.T("Duration"), info?.Duration, textWidth),
            new string('─', textWidth),
        };

        int textAreaLines = Math.Max(1, total - header.Count - 1);
        var wrapped = WrapSpellText(info?.Text, textWidth);
        var pages = new List<List<string>>();
        for (int i = 0; i < wrapped.Count; i += textAreaLines)
            pages.Add(wrapped.Skip(i).Take(textAreaLines).ToList());
        if (pages.Count == 0) pages.Add([]);
        int pageCount = pages.Count;
        page = Math.Clamp(page, 0, pageCount - 1);

        var lines = new List<string>(header);
        foreach (var l in pages[page]) lines.Add(l.PadRight(textWidth));
        while (lines.Count < header.Count + textAreaLines) lines.Add(new string(' ', textWidth));
        lines.Add(pageCount > 1 ? DialogDisplay.NotePageIndicatorText(page, pageCount) : "");

        display.SelectedImageLines = lines.ToArray();
        display.SelectedImageColor = spell.Color;
        display.SelectedImageColorTitleOnly = true;
        display.NotePage = page;
        display.NotePageCount = pageCount;
    }

    private static string SpellField(string label, string? value, int width)
    {
        string text = label.PadRight(SpellFieldLabelWidth) + "  " + (string.IsNullOrEmpty(value) ? "—" : value);
        if (text.Length > width) text = text[..width];
        return text.PadRight(width);
    }

    // "<br>" in the database text separates paragraphs (not a literal line break): lay out each
    // paragraph with its own TextWrapper.WrapText, with a blank line between paragraphs.
    private static List<string> WrapSpellText(string? raw, int width)
    {
        if (string.IsNullOrEmpty(raw)) return [L.T("No description.")];
        var paragraphs = raw.Replace("<br/>", "<br>").Replace("<br />", "<br>").Split("<br>");
        var lines = new List<string>();
        foreach (var p in paragraphs)
        {
            string trimmed = p.Trim();
            if (trimmed.Length == 0) continue;
            if (lines.Count > 0) lines.Add("");
            lines.AddRange(TextWrapper.WrapText(trimmed, width));
        }
        return lines.Count > 0 ? lines : [L.T("No description.")];
    }

    private static string Capitalize(string? s) =>
        string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..];

    private static string LevelToRoman(int level) => level switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V",
        6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX",
        _ => level.ToString(),
    };

    internal static async Task HandleGameScreen(
        WorldState settings, DisplayConfig display, Storage storage, ScreenConfig screen,
        Action draw, Func<string, DialogDisplay?, Task> onInput,
        string title,
        HeroDisplay? heroDisplay = null,
        Action? onScrollUp = null, Action? onScrollDown = null,
        Action? onScrollLeft = null, Action? onScrollRight = null,
        Action? onTab = null, Action? onShiftTab = null,
        Action? onF6 = null, Action? onF7 = null, Action? onF8 = null, Action? onF9 = null, Action? onF10 = null,
        Action<int, int>? onInventorySelect = null,
        Action<int, int>? onSpellSelect = null,
        Action<DialogDisplay?>? setActiveDialog = null,
        Func<DialogDisplay, Action>? pollFactory = null,
        Func<string, bool>? onTextInput = null,
        Action? onDelete = null)
    {
        var titleTabs = ComputeTitleTabs(title);
        int startTop = Console.CursorTop;
        while (true)
        {
            Console.CursorVisible = false;
            Console.SetCursorPosition(0, startTop);
            draw();
            var history = new DialogDisplay(settings, display, storage);
            if (pollFactory != null)
            {
                // The screen's own mouse polling (journal): also used while the AI is answering (DialogDisplay.SwitchTab).
                display.PollAction = pollFactory(history);
                display.JournalPollActionFactory = dlg => pollFactory((DialogDisplay)dlg);
                display.OnTabKey = onTab;
                display.OnShiftTabKey = onShiftTab;
                display.JournalOnTabKey = onTab;
                display.JournalOnShiftTabKey = onShiftTab;
            }
            else if (heroDisplay != null && (onScrollLeft != null || onScrollRight != null))
            {
                display.PollAction = MakeCharacterPollAction(history, heroDisplay, startTop, draw, onScrollLeft, onScrollRight, title, titleTabs, display, onInventorySelect, onSpellSelect);
                var chd = heroDisplay; var cst = startTop; var cdr = draw;
                var csl = onScrollLeft; var csr = onScrollRight;
                var cti = title; var ctb = titleTabs; var ciih = onInventorySelect; var cish = onSpellSelect;
                display.CharacterPollActionFactory = dlg => MakeCharacterPollAction(
                    (DialogDisplay)dlg, chd!, cst, cdr, csl, csr, cti, ctb, display, ciih, cish);
                display.OnTabKey = onTab;
                display.OnShiftTabKey = onShiftTab;
                display.CharacterOnTabKey = onTab;
                display.CharacterOnShiftTabKey = onShiftTab;
            }
            else
            {
                display.PollAction = MakeDialogArrowPollAction(history, title, titleTabs, display);
                var cti2 = title; var ctb2 = titleTabs;
                display.AbilitiesPollActionFactory = dlg => MakeDialogArrowPollAction(
                    (DialogDisplay)dlg, cti2, ctb2, display);
                display.OnTabKey = null;
                display.OnShiftTabKey = null;
            }
            display.RedrawCurrentContent = () =>
            {
                int sl = Console.CursorLeft, st = Console.CursorTop;
                Console.CursorVisible = false;
                Console.SetCursorPosition(0, startTop);
                draw();
                Console.SetCursorPosition(sl, st);
            };
            var input = history.Draw();
            ConsoleMouseReader.SetCursorShape(false);

            if (!string.IsNullOrEmpty(input) && screen.Commands.TryGetValue(input, out var screenAction))
            {
                screenAction();
                display.PollAction = null;
                return;
            }

            // Sound only if something actually pages, as with the mouse (an unavailable arrow stays silent).
            if (input == "Up" && onScrollUp != null) { display.PollAction = null; onScrollUp(); return; }
            if (input == "Down" && onScrollDown != null) { display.PollAction = null; onScrollDown(); return; }
            // The journal pages with an in-place animation (the sound is in the page turn itself); the screen isn't rebuilt.
            if (pollFactory != null && input is "Left" or "Right")
            {
                (input == "Left" ? onScrollLeft : onScrollRight)?.Invoke();
                continue;
            }
            if (input == "Left" && onScrollLeft != null)
            {
                if (heroDisplay == null || heroDisplay.SubTabInfo.canScrollLeft) Sound.PlayClick();
                display.PollAction = null; onScrollLeft(); return;
            }
            if (input == "Right" && onScrollRight != null)
            {
                if (heroDisplay == null || heroDisplay.SubTabInfo.canScrollRight) Sound.PlayClick();
                display.PollAction = null; onScrollRight(); return;
            }
            bool isSpellNote = display.CharacterSubTab == CharacterSubTab.Spells;
            var notePageSelect = isSpellNote ? onSpellSelect : onInventorySelect;
            int notePagePinnedIdx = isSpellNote ? display.PinnedSpellIndex : display.PinnedInventoryIndex;
            if (input == "NoteLeft"  && notePageSelect != null && notePagePinnedIdx >= 0)
            {
                if (display.NotePage > 0) Sound.PlayClick();
                notePageSelect(notePagePinnedIdx, display.NotePage - 1); continue;
            }
            if (input == "NoteRight" && notePageSelect != null && notePagePinnedIdx >= 0)
            {
                if (display.NotePage < display.NotePageCount - 1) Sound.PlayClick();
                notePageSelect(notePagePinnedIdx, display.NotePage + 1); continue;
            }
            if (input == "Tab"      && onTab      != null) { onTab();      while (Console.KeyAvailable) Console.ReadKey(intercept: true); continue; }
            if (input == "ShiftTab" && onShiftTab != null) { onShiftTab(); while (Console.KeyAvailable) Console.ReadKey(intercept: true); continue; }
            if (input == "F6" && onF6 != null) { onF6(); continue; }
            if (input == "F7" && onF7 != null) { onF7(); continue; }
            if (input == "F8" && onF8 != null) { onF8(); continue; }
            if (input == "F9" && onF9 != null) { onF9(); continue; }
            if (input == "F10" && onF10 != null) { onF10(); continue; }
            if (input == "Delete" && onDelete != null) { onDelete(); continue; }
            // Text the screen takes for itself (journal notes) doesn't go to the DM.
            if (!string.IsNullOrEmpty(input) && onTextInput != null && onTextInput(input)) continue;
            // An F-key that does nothing on this screen is ignored rather than sent to the DM as text.
            if (input is not null && input.Length is 2 or 3 && input[0] == 'F' && char.IsDigit(input[1]) && input[1..].All(char.IsDigit)) continue;
            if (input is "Left" or "Right" or "Up" or "Down" or "Tab" or "ShiftTab" or "NoteLeft" or "NoteRight" or "Delete") continue;

            if (!string.IsNullOrEmpty(input))
            {
                settings.History ??= [];
                settings.History.Add(new NaviDnD.Data.Models.DialogMessage { Author = settings.Hero?.Name ?? "Hero", Text = input });
                display.DialogScrollOffset = 0;

                if (setActiveDialog != null)
                {
                    setActiveDialog(history);
                    history.PrepareStreaming();
                    await onInput(input, history); // PollAction stays set throughout streaming
                    history.FinalizeStreaming();
                    setActiveDialog(null);
                }
                else
                {
                    Console.WriteLine();
                    var aiTask = onInput(input, null);
                    await Helpers.Spinner.While(aiTask);
                    await aiTask;
                }
            }
            display.PollAction = null;
            return;
        }
    }
}
