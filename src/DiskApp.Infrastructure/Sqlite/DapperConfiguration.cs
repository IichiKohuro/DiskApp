using System.Data;

using Dapper;

namespace DiskApp.Infrastructure.Sqlite;

/// <summary>
/// Разовая настройка Dapper для SQLite: колонки <c>snake_case</c> сопоставляются свойствам <c>PascalCase</c>,
/// <see cref="DateTimeOffset"/> читается и пишется через <see cref="SqliteTime"/>.
/// </summary>
public static class DapperConfiguration
{
    private static readonly Lock Gate = new();
    private static bool _configured;

    /// <summary>
    /// Применяет настройку один раз на процесс; повторные вызовы ничего не делают.
    /// </summary>
    public static void EnsureConfigured()
    {
        lock (Gate)
        {
            if (_configured)
            {
                return;
            }

            DefaultTypeMap.MatchNamesWithUnderscores = true;

            // Dapper ищет тип сначала во встроенной карте DbType и только потом среди обработчиков:
            // без RemoveTypeMap параметры DateTimeOffset ушли бы в SQLite в его собственном формате.
            SqlMapper.RemoveTypeMap(typeof(DateTimeOffset));
            SqlMapper.RemoveTypeMap(typeof(DateTimeOffset?));
            SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
            _configured = true;
        }
    }

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = SqliteTime.Format(value);
        }

        public override DateTimeOffset Parse(object value) => value switch
        {
            string text => SqliteTime.Parse(text),
            DateTimeOffset offset => offset,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new InvalidCastException($"Невозможно прочитать дату из значения типа {value.GetType().Name}."),
        };
    }
}
