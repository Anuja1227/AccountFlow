
using System;
using System.Collections.Generic;
using System.Linq;

namespace AccountOpeningTracker.Api.Models;

public static class TatHelper
{

    public static DateTime DetermineOriginalInwardDate(
        IEnumerable<Account> relatedAccounts)
    {
        var accounts = relatedAccounts.ToList();

        if (accounts.Count == 0)
        {
            throw new ArgumentException(
                "At least one account record is required.",
                nameof(relatedAccounts));
        }

        return accounts
            .Select(a => a.InwardDate.Date)
            .Min();
    }

    public static void CalculateTat(
        Account account,
        DateTime originalInwardDate,
        DateTime today,
        int slaThresholdWorkingDays)
    {
        account.OriginalInwardDate = originalInwardDate.Date;

        DateTime endDate =
            account.AccountOpeningDate?.Date ?? today.Date;

        account.GrossTatDays = Math.Max(
            0,
            WorkingDaysHelper.CountWorkingDays(
                originalInwardDate, endDate));

        account.NetTatDays = Math.Max(
            0,
            account.GrossTatDays - account.QueryHoldDays);

        account.CurrentAgingDays =
            account.AccountOpeningDate.HasValue
                ? 0
                : Math.Max(
                    0,
                    WorkingDaysHelper.CountWorkingDays(
                        originalInwardDate, today));

        // Flag the case when Gross TAT exceeds the configured threshold.
        account.IsSlaBreached =
            account.GrossTatDays > slaThresholdWorkingDays;
    }
}
