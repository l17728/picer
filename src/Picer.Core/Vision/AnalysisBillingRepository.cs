using Picer.Core.Data;

namespace Picer.Core.Vision;

public sealed class AnalysisBudgetExceededException() : InvalidOperationException("本次请求预计费用与进行中任务预留费用超过日或月预算，请调整预算后重试。");

/// <summary>Anonymous request costs survive replacement of the current description.
/// No paths, content hashes or media identifiers are retained here.</summary>
public sealed class AnalysisBillingRepository(PicerDatabase database)
{
    public decimal GetCostSince(DateTime sinceUtc)
    {
        using var command = database.Command("SELECT COALESCE(SUM(estimated_cost_yuan),0) FROM analysis_billing_events WHERE recorded_utc >= $since");
        command.Parameters.AddWithValue("$since", sinceUtc.ToUniversalTime().ToString("o"));
        return Convert.ToDecimal(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool TryReserve(string requestId, decimal amount, decimal dailyBudget, decimal monthlyBudget)
    {
        var now = DateTime.UtcNow;
        var local = now.ToLocalTime();
        using var command = database.Command("""
            DELETE FROM analysis_budget_reservations WHERE expires_utc <= $now;
            INSERT INTO analysis_budget_reservations(request_id,amount_yuan,created_utc,expires_utc)
            SELECT $id,$amount,$now,$expires
            WHERE (SELECT COALESCE(SUM(estimated_cost_yuan),0) FROM analysis_billing_events WHERE recorded_utc >= $day)
                + (SELECT COALESCE(SUM(amount_yuan),0) FROM analysis_budget_reservations) + CAST($amount AS REAL) <= CAST($daily AS REAL)
              AND (SELECT COALESCE(SUM(estimated_cost_yuan),0) FROM analysis_billing_events WHERE recorded_utc >= $month)
                + (SELECT COALESCE(SUM(amount_yuan),0) FROM analysis_budget_reservations) + CAST($amount AS REAL) <= CAST($monthly AS REAL);
            SELECT changes();
            """);
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$amount", Math.Max(0, amount));
        command.Parameters.AddWithValue("$now", now.ToString("o"));
        command.Parameters.AddWithValue("$expires", now.AddHours(1).ToString("o"));
        command.Parameters.AddWithValue("$day", local.Date.ToUniversalTime().ToString("o"));
        command.Parameters.AddWithValue("$month", new DateTime(local.Year, local.Month, 1).ToUniversalTime().ToString("o"));
        command.Parameters.AddWithValue("$daily", dailyBudget);
        command.Parameters.AddWithValue("$monthly", monthlyBudget);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public void Record(string requestId, ICloudVisionAnalyzer analyzer, CloudVisionUsage? usage)
    {
        using var command = database.Command("""
            INSERT OR IGNORE INTO analysis_billing_events VALUES($id,$provider,$model,$input,$output,$cost,$now);
            DELETE FROM analysis_budget_reservations WHERE request_id=$id;
            """);
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$provider", analyzer.Provider);
        command.Parameters.AddWithValue("$model", analyzer.ModelVersion);
        command.Parameters.AddWithValue("$input", usage?.InputTokens ?? 0);
        command.Parameters.AddWithValue("$output", usage?.OutputTokens ?? 0);
        command.Parameters.AddWithValue("$cost", usage?.EstimatedCostYuan ?? 0m);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        command.ExecuteNonQuery();
    }

    public void Release(string requestId)
    {
        using var command = database.Command("DELETE FROM analysis_budget_reservations WHERE request_id=$id");
        command.Parameters.AddWithValue("$id", requestId);
        command.ExecuteNonQuery();
    }
}
