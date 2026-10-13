using Akiba.Domain.Committee;
using Akiba.Domain.Financial;
using Akiba.Infrastructure.Persistence.Rows;

namespace Akiba.Infrastructure.Persistence;

internal static class CommitteeMapper
{
    public static CommitteePositionRow ToRow(CommitteePosition position)
    {
        ArgumentNullException.ThrowIfNull(position);

        return new CommitteePositionRow
        {
            Id = position.Id.Value,
            Name = position.Name,
            Description = position.Description,
            AllowanceAmount = position.Allowance.Amount,
            AllowanceCurrencyCode = position.Allowance.Currency.Code,
            Frequency = (int)position.Frequency,
            IsActive = position.IsActive,
        };
    }

    public static CommitteePosition ToDomain(CommitteePositionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return CommitteePosition.Rehydrate(
            new CommitteePositionId(row.Id),
            row.Name,
            row.Description,
            new Money(row.AllowanceAmount, Currency.Of(row.AllowanceCurrencyCode)),
            (AllowanceFrequency)row.Frequency,
            row.IsActive);
    }

    public static CommitteeAppointmentRow ToRow(CommitteeAppointment appointment)
    {
        ArgumentNullException.ThrowIfNull(appointment);

        return new CommitteeAppointmentRow
        {
            Id = appointment.Id.Value,
            PositionId = appointment.PositionId.Value,
            PositionName = appointment.PositionName,
            HolderName = appointment.HolderName,
            AppointedOn = appointment.AppointedOn,
            Term = appointment.Term,
            Status = (int)appointment.Status,
            SuspendedReason = appointment.SuspendedReason,
            EndedOn = appointment.EndedOn,
        };
    }

    public static CommitteeAppointment ToDomain(CommitteeAppointmentRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return CommitteeAppointment.Rehydrate(
            new CommitteeAppointmentId(row.Id),
            new CommitteePositionId(row.PositionId),
            row.PositionName,
            row.HolderName,
            row.AppointedOn,
            row.Term,
            (AppointmentStatus)row.Status,
            row.SuspendedReason,
            row.EndedOn);
    }

    public static ElectionRecordRow ToRow(ElectionRecord election)
    {
        ArgumentNullException.ThrowIfNull(election);

        var row = new ElectionRecordRow
        {
            Id = election.Id.Value,
            HeldOn = election.HeldOn,
            Term = election.Term,
            Notes = election.Notes,
            Status = (int)election.Status,
        };

        row.Outcomes =
        [
            .. election.Outcomes.Select(o => new ElectionOutcomeRow
            {
                Id = o.Id,
                ElectionId = election.Id.Value,
                PositionId = o.PositionId.Value,
                PositionName = o.PositionName,
                ElectedName = o.ElectedName,
            }),
        ];

        return row;
    }

    public static ElectionRecord ToDomain(ElectionRecordRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return ElectionRecord.Rehydrate(
            new ElectionId(row.Id),
            row.HeldOn,
            row.Term,
            row.Notes,
            (ElectionStatus)row.Status,
            row.Outcomes.Select(o => new ElectionOutcome(
                o.Id,
                new CommitteePositionId(o.PositionId),
                o.PositionName,
                o.ElectedName)));
    }
}
