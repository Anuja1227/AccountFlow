
using System;

namespace AccountOpeningTracker.Api.Models;

public static class WorkingDaysHelper
{
    public static int CountWorkingDays(
        DateTime startDate,
        DateTime endDate)
    {
        int workingDays = 0;

        DateTime currentDate = startDate.Date.AddDays(1);
        DateTime finalDate = endDate.Date;

        while (currentDate <= finalDate)
        {
            if (currentDate.DayOfWeek != DayOfWeek.Saturday &&
                currentDate.DayOfWeek != DayOfWeek.Sunday)
            {
                workingDays++;
            }

            currentDate = currentDate.AddDays(1);
        }

        return workingDays;
    }
}
