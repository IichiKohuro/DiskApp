using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DiskApp.Desktop.SingleInstance;

/// <summary>
/// Имена мьютекса и именованного канала одиночного экземпляра. Привязаны к пользователю и сеансу,
/// чтобы два пользователя на одной машине (или два сеанса) могли запускать приложение независимо.
/// Портативная копия привязана ещё и к своей папке данных: у неё своя база, и она не мешает установленной копии.
/// </summary>
/// <param name="Mutex">Имя мьютекса с префиксом <c>Local\</c> (область — сеанс).</param>
/// <param name="Pipe">Имя именованного канала.</param>
internal sealed record InstanceNames(string Mutex, string Pipe)
{
    /// <summary>
    /// Имена для продукта в текущем сеансе текущего пользователя.
    /// </summary>
    /// <param name="product">Имя продукта.</param>
    /// <param name="dataScope">Папка данных портативной копии; <c>null</c> — обычная установка, один экземпляр на сеанс.</param>
    public static InstanceNames ForCurrentUser(string product = "DiskApp", string? dataScope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(product);

        var session = Environment.ProcessId == 0 ? 0 : Process.GetCurrentProcess().SessionId;
        var suffix = $"{product}.{Sanitize(Environment.UserName)}.{session.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(dataScope))
        {
            suffix += "." + ScopeHash(dataScope);
        }

        return new InstanceNames(@"Local\" + suffix + ".Instance", suffix + ".Pipe");
    }

    private static string ScopeHash(string dataScope)
    {
        var normalized = Path.GetFullPath(dataScope).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)), 0, 6);
    }

    private static string Sanitize(string value)
        => string.Concat(value.Select(static c => char.IsLetterOrDigit(c) ? c : '_'));
}
