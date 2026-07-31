using System.Reflection;
using Acta.Runtime.Modules.Execution.Schedules;
using Xunit;

namespace Acta.Tests.Abstractions;

/// <summary>
/// Guards every <see cref="Cron"/> constant: each must be a valid, satisfiable cron expression under
/// the same parse path the runtime uses (<see cref="NextOccurrenceCalculator.Next"/>, which selects the
/// 5- vs 6-field Cronos format by field count). Catches a typo'd or unsatisfiable constant at build
/// time rather than when a schedule first fires.
/// </summary>
public sealed class CronTests
{
    public static TheoryData<string, string> AllConstants()
    {
        var data = new TheoryData<string, string>();
        foreach (var field in typeof(Cron).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            {
                data.Add(field.Name, (string)field.GetRawConstantValue()!);
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllConstants))]
    public void Constant_is_a_valid_and_satisfiable_cron_expression(string name, string expression)
    {
        var anchor = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var next = NextOccurrenceCalculator.Next(expression, timeZone: null, ScheduleExpressionKindCode.Cron, anchor);
        Assert.True(next is not null, $"Cron.{name} ('{expression}') produced no next occurrence.");
    }
}
