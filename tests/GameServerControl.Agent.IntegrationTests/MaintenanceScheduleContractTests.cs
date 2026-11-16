using GameServerControl.Agent.Servers;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class MaintenanceScheduleContractTests
{
    [Fact]
    public void Task_xml_round_trips_weekday_without_english_console_output()
    {
        const string xml = """
<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers><CalendarTrigger><StartBoundary>2026-11-18T04:00:00</StartBoundary>
    <ScheduleByWeek><DaysOfWeek><Friday /></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek>
  </CalendarTrigger></Triggers>
</Task>
""";
        var (start, weekday) = MaintenanceScheduler.ParseTaskXml(xml);
        Assert.Equal(4, start?.Hour);
        Assert.Equal(DayOfWeek.Friday, weekday);
    }

    [Fact]
    public void Invalid_task_xml_returns_no_schedule()
    {
        var (start, weekday) = MaintenanceScheduler.ParseTaskXml("not xml");
        Assert.Null(start);
        Assert.Null(weekday);
    }
}
