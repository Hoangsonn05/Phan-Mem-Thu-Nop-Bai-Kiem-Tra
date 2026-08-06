using ExamTransfer.Desktop.Services;
namespace ExamTransfer.Desktop.Tests;

public class MvmTest {
    public void Run() {
        // can we touch AppServices?
        var x = AppServices.StudentState;
    }
}
