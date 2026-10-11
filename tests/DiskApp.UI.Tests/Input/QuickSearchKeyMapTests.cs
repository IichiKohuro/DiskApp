using Avalonia.Input;

using DiskApp.UI.Input;

namespace DiskApp.UI.Tests.Input;

/// <summary>
/// Карта клавиш быстрого поиска (PLAN.md, этап 7.2): команды списка и действий, клавиши редактирования остаются полю.
/// </summary>
public sealed class QuickSearchKeyMapTests
{
    private static readonly QueryBoxState Typed = new(IsEmpty: false, HasSelection: false, CaretAtEnd: true);
    private static readonly QueryBoxState Empty = new(IsEmpty: true, HasSelection: false, CaretAtEnd: true);
    private static readonly QueryBoxState Selected = new(IsEmpty: false, HasSelection: true, CaretAtEnd: true);
    private static readonly QueryBoxState CaretInside = new(IsEmpty: false, HasSelection: false, CaretAtEnd: false);

    [Theory]
    [InlineData(Key.Down, KeyModifiers.None, QuickSearchCommand.MoveDown)]
    [InlineData(Key.Up, KeyModifiers.None, QuickSearchCommand.MoveUp)]
    [InlineData(Key.PageDown, KeyModifiers.None, QuickSearchCommand.PageDown)]
    [InlineData(Key.PageUp, KeyModifiers.None, QuickSearchCommand.PageUp)]
    [InlineData(Key.Home, KeyModifiers.Control, QuickSearchCommand.First)]
    [InlineData(Key.End, KeyModifiers.Control, QuickSearchCommand.Last)]
    [InlineData(Key.Enter, KeyModifiers.None, QuickSearchCommand.Open)]
    [InlineData(Key.Enter, KeyModifiers.Control, QuickSearchCommand.Reveal)]
    [InlineData(Key.Enter, KeyModifiers.Shift, QuickSearchCommand.OpenWith)]
    [InlineData(Key.Enter, KeyModifiers.Alt, QuickSearchCommand.Properties)]
    [InlineData(Key.Enter, KeyModifiers.Control | KeyModifiers.Shift, QuickSearchCommand.RunAsAdmin)]
    [InlineData(Key.C, KeyModifiers.Control, QuickSearchCommand.CopyFiles)]
    [InlineData(Key.C, KeyModifiers.Control | KeyModifiers.Shift, QuickSearchCommand.CopyPath)]
    [InlineData(Key.F2, KeyModifiers.None, QuickSearchCommand.Rename)]
    [InlineData(Key.Delete, KeyModifiers.None, QuickSearchCommand.Delete)]
    [InlineData(Key.P, KeyModifiers.Control, QuickSearchCommand.TogglePin)]
    [InlineData(Key.Tab, KeyModifiers.None, QuickSearchCommand.ContextMenu)]
    [InlineData(Key.F10, KeyModifiers.Shift, QuickSearchCommand.ContextMenu)]
    [InlineData(Key.Apps, KeyModifiers.None, QuickSearchCommand.ContextMenu)]
    [InlineData(Key.Escape, KeyModifiers.None, QuickSearchCommand.Escape)]
    public void Resolve_TypedQuery_MapsKeyToCommand(Key key, KeyModifiers modifiers, QuickSearchCommand expected)
    {
        // Act
        var resolved = QuickSearchKeyMap.Resolve(key, modifiers, Typed);

        // Assert
        resolved.ShouldBe(new QuickSearchKey(expected));
    }

    [Theory]
    [InlineData(Key.D1, 1)]
    [InlineData(Key.D9, 9)]
    [InlineData(Key.NumPad4, 4)]
    public void Resolve_CtrlDigit_OpensNumberedRow(Key key, int number)
    {
        // Act & Assert
        QuickSearchKeyMap.Resolve(key, KeyModifiers.Control, Typed).ShouldBe(new QuickSearchKey(QuickSearchCommand.OpenNumber, number));
    }

    [Fact]
    public void Resolve_HomeEnd_BelongToTextUnlessQueryIsEmpty()
    {
        // Act & Assert
        QuickSearchKeyMap.Resolve(Key.Home, KeyModifiers.None, Typed).ShouldBeNull("курсор в начало текста");
        QuickSearchKeyMap.Resolve(Key.End, KeyModifiers.None, Typed).ShouldBeNull();
        QuickSearchKeyMap.Resolve(Key.Home, KeyModifiers.None, Empty).ShouldBe(new QuickSearchKey(QuickSearchCommand.First));
        QuickSearchKeyMap.Resolve(Key.End, KeyModifiers.None, Empty).ShouldBe(new QuickSearchKey(QuickSearchCommand.Last));
    }

    [Fact]
    public void Resolve_CtrlCWithSelectedText_CopiesText()
    {
        // Act & Assert
        QuickSearchKeyMap.Resolve(Key.C, KeyModifiers.Control, Selected).ShouldBeNull();
    }

    [Fact]
    public void Resolve_DeleteInsideOrOverSelection_EditsText()
    {
        // Act & Assert
        QuickSearchKeyMap.Resolve(Key.Delete, KeyModifiers.None, CaretInside).ShouldBeNull("удаляет символ справа");
        QuickSearchKeyMap.Resolve(Key.Delete, KeyModifiers.None, Selected).ShouldBeNull("удаляет выделение");
        QuickSearchKeyMap.Resolve(Key.Delete, KeyModifiers.None, Empty).ShouldBe(new QuickSearchKey(QuickSearchCommand.Delete));
    }

    [Theory]
    [InlineData(Key.A, KeyModifiers.None)]
    [InlineData(Key.Left, KeyModifiers.None)]
    [InlineData(Key.Back, KeyModifiers.None)]
    [InlineData(Key.A, KeyModifiers.Control)]
    [InlineData(Key.V, KeyModifiers.Control)]
    public void Resolve_TextEditingKeys_StayWithQueryBox(Key key, KeyModifiers modifiers)
    {
        // Act & Assert
        QuickSearchKeyMap.Resolve(key, modifiers, Typed).ShouldBeNull();
    }

    [Fact]
    public void Gestures_MenuCommands_HaveLabels()
    {
        // Act & Assert
        QuickSearchKeyMap.RevealText.ShouldBe("Ctrl+Enter");
        QuickSearchKeyMap.RunAsAdminText.ShouldBe("Ctrl+Shift+Enter");
        QuickSearchKeyMap.CopyPathText.ShouldBe("Ctrl+Shift+C");
        QuickSearchKeyMap.PropertiesText.ShouldBe("Alt+Enter");
        QuickSearchKeyMap.DeleteText.ShouldBe("Del");
        QuickSearchKeyMap.RenameText.ShouldBe("F2");
        QuickSearchKeyMap.GestureText(QuickSearchCommand.MoveDown).ShouldBeEmpty();
        QuickSearchKeyMap.Gestures.Keys.ShouldBeSubsetOf(Enum.GetValues<QuickSearchCommand>());
        foreach (var (command, gesture) in QuickSearchKeyMap.Gestures)
        {
            var state = command == QuickSearchCommand.Delete ? Empty : Typed;
            QuickSearchKeyMap.Resolve(gesture.Key, gesture.KeyModifiers, state).ShouldBe(new QuickSearchKey(command), $"подпись {gesture} совпадает с картой");
        }
    }
}
