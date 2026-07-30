namespace ExamTransfer.LocalServer;

public static class LocalServerSingleInstance
{
    public static Mutex Acquire()
    {
        var mutex = new Mutex(
            initiallyOwned: false,
            name: @"Local\ExamTransfer.LocalServer.SingleInstance");
        try
        {
            if (mutex.WaitOne(0))
                return mutex;
        }
        catch (AbandonedMutexException)
        {
            return mutex;
        }

        mutex.Dispose();
        throw new InvalidOperationException(
            "ExamTransfer Local Server is already running in another process.");
    }
}
