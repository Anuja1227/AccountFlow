
using System.Collections.Generic;
using System.Linq;

namespace AccountOpeningTracker.Api.Models;

public static class RftHelper
{
    public static string DetermineRftStatus(
        IEnumerable<Account> accounts)
    {
        foreach (var account in accounts)
        {
            bool hadStage1Query =
                account.Stage1QueryRaisedDate.HasValue;

            bool hadStage3Discrepancy =
                account.DiscrepancyRaisedDate.HasValue ||
                account.FormReturnedToCseDate.HasValue;

            bool hadMofslQuery =
                account.MofslQueryRaisedDate.HasValue;

            if (hadStage1Query ||
                hadStage3Discrepancy ||
                hadMofslQuery)
            {
                return "NRFT";
            }
        }

        return "RFT";
    }
}
