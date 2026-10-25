namespace QingSnap.App.Models;

public sealed record SelectionPreset(
    Guid Id,
    string Name,
    SelectionSet Selection,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
