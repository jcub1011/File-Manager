using FileManager.Contracts.Primitives;
using System.Collections.Generic;

namespace FileManager.Core.Transformers;

public interface IArgumentParser
{
    Result<IReadOnlyList<string>, string> Parse(string arguments);
}
