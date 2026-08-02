BASE_COMMIT: db55788ece6973e321c9cad06904dc997db0cb6a
BRANCH: work/person-b-frontend
SOURCE_REMOTE: https://github.com/Hoangsonn05/Phan-Mem-Thu-Nop-Bai-Kiem-Tra.git
TARGET_REMOTE: https://github.com/ManhTien-360cm/Phan-Mem-Thu-Nop-Bai-Kiem-Tra.git
DOTNET_SDK: 10.0.302
FRONTEND_BUILD: PASS — Debug solution build succeeded with 0 warnings and 0 errors.
FRONTEND_TESTS: FAIL — 209 passed, 2 failed, 0 skipped, 211 total.
PRE_EXISTING_FAILURES:
- BulkArchiveCheckboxWpfTests.RealCheckboxWpfClick_ImmediatelyUpdatesExactRowsCountAndHeader
- StudentConnectWpfTests.PublicCloudRoomCode_RealControlUpdatesImmediatelyAndSurvivesModeToggle
- Both failures originate from WPF static initialization in MS.Internal.FontCache.Util with System.UriFormatException ("Invalid URI: The format of the URI could not be determined.").
- `dotnet --info` reports SDK 10.0.302, then its Windows workload information path throws TypeInitializationException in Microsoft.DotNet.Cli.Installer.Windows.InstallerBase. Restore and build still complete successfully.
DATE: 2026-08-01T16:56:15+07:00
RESULT: CONTINUE — production build succeeds and the pre-existing environment-specific WPF failures do not block isolated PublicCloud join mapping tests.
