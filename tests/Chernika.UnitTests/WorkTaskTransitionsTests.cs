using Chernika.Domain;
using Chernika.Domain.Enums;

namespace Chernika.UnitTests;

public class WorkTaskTransitionsTests
{
    [Theory]
    [InlineData(WorkTaskStatus.Completed)]
    [InlineData(WorkTaskStatus.Cancelled)]
    public void IsTerminal_ReturnsTrue_ForClosedStates(WorkTaskStatus status)
    {
        Assert.True(WorkTaskTransitions.IsTerminal(status));
        Assert.False(WorkTaskTransitions.CanModify(status));
    }

    [Theory]
    [InlineData(WorkTaskStatus.Open)]
    [InlineData(WorkTaskStatus.InProgress)]
    [InlineData(WorkTaskStatus.Overdue)]
    public void IsTerminal_ReturnsFalse_ForActiveStates(WorkTaskStatus status)
    {
        Assert.False(WorkTaskTransitions.IsTerminal(status));
        Assert.True(WorkTaskTransitions.CanModify(status));
    }

    [Theory]
    [InlineData(WorkTaskStatus.Open, true)]
    [InlineData(WorkTaskStatus.Overdue, true)]
    [InlineData(WorkTaskStatus.InProgress, false)]
    [InlineData(WorkTaskStatus.Completed, false)]
    [InlineData(WorkTaskStatus.Cancelled, false)]
    public void CanStart_OnlyAllowsOpenAndOverdue(WorkTaskStatus status, bool expected)
    {
        Assert.Equal(expected, WorkTaskTransitions.CanStart(status));
    }

    [Theory]
    [InlineData(WorkTaskStatus.Open, true)]
    [InlineData(WorkTaskStatus.InProgress, true)]
    [InlineData(WorkTaskStatus.Overdue, true)]
    [InlineData(WorkTaskStatus.Completed, false)]
    [InlineData(WorkTaskStatus.Cancelled, false)]
    public void IsActive_OnlyCountsOpenInProgressOverdue(WorkTaskStatus status, bool expected)
    {
        Assert.Equal(expected, WorkTaskTransitions.IsActive(status));
    }
}
