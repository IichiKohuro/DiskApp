using System.Xml.Linq;

namespace DiskApp.UI.Tests.Accessibility;

/// <summary>
/// Доступность разметки (PLAN.md, этап 9.4): у каждого интерактивного элемента есть имя для Экранного диктора. Кнопка
/// с текстом получает имя из текста; кнопка со значком или составным содержимым без <c>AutomationProperties.Name</c>
/// звучала бы как «Avalonia.Controls.StackPanel». Поля ввода, списки и переключатели без подписи в самом элементе тоже
/// должны быть названы.
/// </summary>
public sealed class AxamlAccessibilityTests
{
    private static readonly string[] Folders = ["src/DiskApp.UI", "src/DiskApp.Desktop"];

    // Элементы, у которых имя берётся из Content, если это текст.
    private static readonly HashSet<string> ContentControls = new(StringComparer.Ordinal)
    {
        "Button", "ToggleButton", "RepeatButton", "HyperlinkButton", "DropDownButton", "SplitButton", "ToggleSplitButton", "CheckBox", "RadioButton",
    };

    // Элементы без своего текста: имя нужно всегда.
    private static readonly HashSet<string> NamedControls = new(StringComparer.Ordinal)
    {
        "TextBox", "AutoCompleteBox", "ComboBox", "ListBox", "Slider", "NumericUpDown", "TreeView", "ToggleSwitch", "FANumberBox",
    };

    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    [Fact]
    public void InteractiveElements_HaveAccessibleNames()
    {
        // Arrange
        var root = RepositoryRoot();
        var files = Folders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.axaml", SearchOption.AllDirectories))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();

        // Act
        var problems = new List<string>();
        foreach (var file in files)
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants())
            {
                if (NeedsName(element) && !HasName(element))
                {
                    var line = ((System.Xml.IXmlLineInfo)element).LineNumber;
                    problems.Add($"{Path.GetFileName(file)}:{line} <{element.Name.LocalName}>");
                }
            }
        }

        // Assert
        files.Length.ShouldBeGreaterThan(10);
        problems.ShouldBeEmpty();
    }

    private static bool NeedsName(XElement element)
    {
        var name = element.Name.LocalName.Replace("FA", string.Empty, StringComparison.Ordinal);
        if (element.Name.Namespace != Avalonia && !element.Name.LocalName.StartsWith("FA", StringComparison.Ordinal))
        {
            return false;
        }

        if (NamedControls.Contains(element.Name.LocalName) || NamedControls.Contains(name))
        {
            // Подписи «Вкл»/«Выкл» переключателя не становятся его именем: имя нужно и ему.
            return true;
        }

        if (!ContentControls.Contains(name))
        {
            return false;
        }

        // Текст в Content (строка, ресурс или привязка к строке) — имя есть; дочерний элемент вместо текста — имени нет.
        var content = element.Attribute("Content")?.Value;
        if (content is not null)
        {
            return false;
        }

        var children = element.Elements().Where(static child => !child.Name.LocalName.Contains('.', StringComparison.Ordinal)).ToList();
        if (children.Count == 0)
        {
            return element.Nodes().OfType<XText>().All(static text => string.IsNullOrWhiteSpace(text.Value));
        }

        return true;
    }

    private static bool HasName(XElement element)
        => element.Attributes().Any(static attribute => attribute.Name.LocalName == "AutomationProperties.Name");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DiskApp.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Не найден корень репозитория");
    }
}
