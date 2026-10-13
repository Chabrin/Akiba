namespace Akiba.Infrastructure.Persistence.Rows;

internal sealed class CommitteePositionRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public decimal AllowanceAmount { get; set; }

    public string AllowanceCurrencyCode { get; set; } = "KES";

    /// <summary>1 = Monthly, 2 = PerSitting.</summary>
    public int Frequency { get; set; }

    public bool IsActive { get; set; } = true;
}

internal sealed class CommitteeAppointmentRow
{
    public Guid Id { get; set; }

    public Guid PositionId { get; set; }

    public string PositionName { get; set; } = string.Empty;

    public string HolderName { get; set; } = string.Empty;

    public DateOnly AppointedOn { get; set; }

    public string Term { get; set; } = string.Empty;

    /// <summary>1 = Active, 2 = Suspended, 3 = Ended.</summary>
    public int Status { get; set; }

    public string? SuspendedReason { get; set; }

    public DateOnly? EndedOn { get; set; }
}

internal sealed class ElectionRecordRow
{
    public Guid Id { get; set; }

    public DateOnly HeldOn { get; set; }

    public string Term { get; set; } = string.Empty;

    public string? Notes { get; set; }

    /// <summary>1 = Draft, 2 = Ratified.</summary>
    public int Status { get; set; }

    public List<ElectionOutcomeRow> Outcomes { get; set; } = [];
}

internal sealed class ElectionOutcomeRow
{
    public Guid Id { get; set; }

    public Guid ElectionId { get; set; }

    public Guid PositionId { get; set; }

    public string PositionName { get; set; } = string.Empty;

    public string ElectedName { get; set; } = string.Empty;
}
