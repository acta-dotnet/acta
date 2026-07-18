using Acta;

namespace TestJobs;

// Regression probes for scalar value-type inputs. A bare int / double must round-trip through the
// typed enqueue path. Guards the generator fix where value types were misclassified as the 'none'
// payload format and silently arrived at the handler as default(T).
public static class ScalarEchoHandler
{
    [Job("scalar-int-echo")]
    public static int EchoInt(int input) => input * 2;

    [Job("scalar-double-echo")]
    public static double EchoDouble(double input) => input / 2;
}
