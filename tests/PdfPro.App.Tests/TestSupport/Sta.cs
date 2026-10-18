using System.Runtime.ExceptionServices;

namespace PdfPro.App.Tests.TestSupport;

/// <summary>Runs code on a fresh STA thread, as WPF documents and visuals require.</summary>
internal static class Sta
{
    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }

        return result;
    }

    public static void Run(Action work) => Run(() =>
    {
        work();
        return 0;
    });
}
