using Npgsql;

namespace Nexora.Data.Persistence;

public static class DatabaseConnectivityFailure
{
    // Do not classify constraints, serialization failures or arbitrary application
    // exceptions as connectivity faults. Never replay the failed transaction here.
    public static bool IsTransient(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
                return postgres.SqlState.StartsWith("08", StringComparison.Ordinal) ||
                    postgres.SqlState is "57P01" or "57P02" or "57P03" or "53300";
            if (current is NpgsqlException npgsql)
                return npgsql.IsTransient;
        }
        return false;
    }
}
