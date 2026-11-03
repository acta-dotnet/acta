namespace Acta;

/// <summary>Whether the catalog's outbox table exists as a table; another kind of object with that name does not count.</summary>
internal readonly record struct FindOutboxTableResponse(bool Exists);
