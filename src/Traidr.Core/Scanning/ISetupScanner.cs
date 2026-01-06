using Traidr.Core.MarketData;

namespace Traidr.Core.Scanning;

public interface ISetupScanner
{
    IReadOnlyList<SetupCandidate> Scan(IReadOnlyList<Bar> barsForManySymbols);
    IReadOnlyList<SetupCandidate> Scan(IReadOnlyDictionary<string, List<Bar>> barsBySymbolOrdered);
}
