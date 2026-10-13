using Akiba.Application.Abstractions;
using MediatR;

namespace Akiba.Application.Identity;

/// <summary>Returns every official account, ordered by username.</summary>
public sealed record ListOfficialsQuery : IRequest<IReadOnlyList<OfficialSummary>>;

internal sealed class ListOfficialsHandler
    : IRequestHandler<ListOfficialsQuery, IReadOnlyList<OfficialSummary>>
{
    private readonly IOfficialRepository _officials;

    public ListOfficialsHandler(IOfficialRepository officials)
    {
        _officials = officials;
    }

    public Task<IReadOnlyList<OfficialSummary>> Handle(
        ListOfficialsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _officials.ListAsync(cancellationToken);
    }
}
