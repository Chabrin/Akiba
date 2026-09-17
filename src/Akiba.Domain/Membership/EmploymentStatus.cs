namespace Akiba.Domain.Membership;

/// <summary>
/// Whether a member still works for Chabrin Agencies.
/// </summary>
/// <remarks>
/// This matters far beyond record-keeping. Repayment is by salary deduction at source, so an
/// employed member cannot miss a payment and an exited one can. Guaranteeing also requires
/// current employment, so an exit flags every loan the member guarantees.
/// </remarks>
public enum EmploymentStatus
{
    /// <summary>A current CAL employee. Deductions run at source.</summary>
    Employed = 1,

    /// <summary>
    /// No longer with CAL. Any loan balance is recovered from final dues, and a shortfall
    /// passes to the guarantors.
    /// </summary>
    Exited = 2,
}
