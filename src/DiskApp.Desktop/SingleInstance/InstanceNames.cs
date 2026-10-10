using System.Diagnostics;
using System.Globalization;

namespace DiskApp.Desktop.SingleInstance;

/// <summary>
/// Имена мьютекса и именованного канала одиночного экземпляра. Привязаны к пользователю и сеансу,
/// чтобы два пользователя на одной машине (или два сеанса) могли запускать приложение независимо.
/// </summary>
/// <param name="Mutex">Имя мьютекса с префиксом <c>Local\</c> (область — сеанс).</param>
/// <param name="Pipe">Имя именованного канала.</param>
internal sealed record InstanceNames(string Mutex, string Pipe)
{
    /// <summary>
    /// Имена для продукта в текущем сеансе текущего пользователя.
    /// </summary>
    public static InstanceNames ForCurrentUser(string product = "DiskApp")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(product);

        var session = Environment.ProcessId == 0 ? 0 : Process.GetCurrentProcess().SessionId;
        var suffix = $"{product}.{Sanitize(Environment.UserName)}.{session.ToString(CultureInfo.InvariantCulture)}";
        return new InstanceNames(@"Local\" + suffix + ".Instance", suffix + ".Pipe");
    }

    private static string Sanitize(string value)
        => string.Concat(value.Select(static c => char.IsLetterOrDigit(c) ? c : '_'));
}
