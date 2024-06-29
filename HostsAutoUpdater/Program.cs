using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32.TaskScheduler;

namespace HostsAutoUpdater {
    internal class Program {

        [DllImport("User32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow([In] IntPtr hWnd, [In] int nCmdShow);

        const String BEGIN_SELF_HOSTS = "#START HostsAutoUpdater List - DO NOT EDIT THIS LINE";
        const String END_SELF_HOSTS   = "#END HostsAutoUpdater List - DO NOT EDIT THIS LINE";
        
        const String TASK_NAME = "HostsAutoUpdater";
        static void Main(string[] args) {
            List<string> blocklistList = new List<string>();

            if(!IsUserAdministrator()) {
                Console.WriteLine($"This program must be run with admin rights");
                Console.ReadKey();
                return;
            }

            DoInstallation();
            
            IntPtr handle = Process.GetCurrentProcess().MainWindowHandle;
            ShowWindow(handle, 6);
#if DEBUG
            string hostsPath = "hosts";
#else
            string hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                            "System32\\drivers\\etc\\hosts");
#endif

            if(!File.Exists(hostsPath)) {
                Console.WriteLine($"No hosts file found in path {hostsPath}");
                Console.ReadKey();
                return;
            }

            if(File.Exists("blocklist.txt")) {
                blocklistList = File.ReadAllLines("blocklist.txt").ToList();
                blocklistList = blocklistList.Distinct().ToList();
            }

            if(blocklistList.Count == 0) {
                Console.WriteLine("No block list to add");
                Console.ReadKey();
                return;
            }
            Console.WriteLine($"Found {blocklistList.Count} block list/s");


            String[] currHosts = File.ReadAllLines(hostsPath);

            HashSet<String> allowListURL = new HashSet<String>();
            if(File.Exists("allowlist.txt")) {
                allowListURL = File.ReadAllLines("allowlist.txt").ToHashSet();
            }
            Console.WriteLine($"Found {allowListURL.Count} allowed URLs");

           
            //List<string> selfHostLineStr   = new List<string>();
            List<string> noselfHostLineStr = new List<string>();

            int idxBeginSelf = -1;
            int idxEndSelf = -1;
            for (int i = 0; i < currHosts.Length; i++) {
                string currLine = currHosts[i];

                if(currLine == BEGIN_SELF_HOSTS) {
                    idxBeginSelf = i;
                    continue;
                }
                
                if(currLine == END_SELF_HOSTS) {
                    idxEndSelf = i;
                    continue;
                }

                if (   idxBeginSelf != -1
                    && idxEndSelf   == -1 ) {
                    //selfHostLineStr.Add(currHost);
                } else {
                    noselfHostLineStr.Add(currLine);
                }
            }
            HashSet<string> noselfHostLineURL = noselfHostLineStr.Where (x =>   !x.StartsWith('#')              //No to comments, yes to Valsoia
                                                                              && (   x.StartsWith("0.0.0.0")
                                                                                  || x.StartsWith("127.0.0.1")) //We only care about link to null
                                                                              && x.Trim().Length > 0)           //Skip empty lines
                                                                 .Select(x => new Regex("([0-9.:]+)\\s+([\\w.-]+)($|\\s+(\\#.*))").Match(x).Groups[2].Value)
                                                                 .ToHashSet();
            
            Console.WriteLine($"Found {noselfHostLineURL.Count} URL already blocked");

            //HashSet<string> selfHostLineURL = selfHostLineStr.Select(x => new Regex("([0-9.:]+)\\s+([\\w.-]+)($|\\s+(\\#.*))").Match(x).Groups[2].Value).ToHashSet();

            List<string> allBlocklistURL = new List<string>();
            foreach(string blocklist in blocklistList) {
                Console.WriteLine($"Downloading {blocklist}");
                try {
                    List<string> currBlocklistURL = new WebClient().DownloadString(blocklist)
                                                                   .Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)
                                                                   .Where(x =>   !x.StartsWith('#')             
                                                                               && (   x.StartsWith("0.0.0.0")
                                                                                   || x.StartsWith("127.0.0.1"))
                                                                               && x.Trim().Length > 0)
                                                                   .Select(x => new Regex("([0-9.:]+)\\s+([\\w.-]+)($|\\s+(\\#.*))").Match(x).Groups[2].Value)
                                                                   .ToList();

                    Console.WriteLine($"Found {currBlocklistURL.Count} URL in this list");

                    allBlocklistURL.AddRange(currBlocklistURL);

                }catch (Exception e) {
                    Console.WriteLine($"Download of this list returned error: {e.Message}");
                }
            }

            allBlocklistURL = allBlocklistURL.Distinct().ToList();
            Console.WriteLine($"Found {allBlocklistURL.Count} URL in total");

            allBlocklistURL = allBlocklistURL.Where(x => !allowListURL.Contains(x)).ToList();
            Console.WriteLine($"Found {allBlocklistURL.Count} URL after allow list");
            
            allBlocklistURL = allBlocklistURL.Where(x => !noselfHostLineURL.Contains(x)).ToList();
            Console.WriteLine($"Found {allBlocklistURL.Count} URL not already blocked");
            
            List<string> newHostsFile = new List<string>();
            newHostsFile.AddRange(noselfHostLineStr);

            newHostsFile.Add(BEGIN_SELF_HOSTS);
            newHostsFile.AddRange(allBlocklistURL.Select(x => "0.0.0.0\t" + x).ToList());
            newHostsFile.Add(END_SELF_HOSTS);

            File.Copy(hostsPath, hostsPath + ".bak", true);
            File.Delete(hostsPath);
            File.WriteAllLines(hostsPath, newHostsFile);

            Thread.Sleep(30000);
        }
        public static bool IsUserAdministrator()
        {
            bool isAdmin;
            try
            {
                WindowsIdentity user = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(user);
                isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (UnauthorizedAccessException ex)
            {
                isAdmin = false;
            }
            catch (Exception ex)
            {
                isAdmin = false;
            }
            return isAdmin;
        }

        public static void DoInstallation() {

            if(File.Exists(".init")) {
                return;
            }

            if(TaskService.Instance.GetTask(TASK_NAME) != null){
                TaskService.Instance.RootFolder.DeleteTask(TASK_NAME);
                Console.WriteLine("Old scheduled task was deleted");
            }
            
            bool doSchedule = false;

            Console.WriteLine("Would you like to schedule a weekly task? [y/n]");
            if(Console.ReadKey().KeyChar == 'y') {
                doSchedule = true;
            }
            Console.WriteLine();

            string pathAllowList = "allowlist.txt";
            string pathBlockList = "blocklist.txt";

            if(!File.Exists(pathAllowList)) {
                File.Create(pathAllowList).Close();
            }

            if(!File.Exists(pathBlockList)) {
                File.Create(pathBlockList).Close();
            }

            if(doSchedule) {
                int day  = -1;
                int hour = -1;

                bool tryAgain = true;
                Console.WriteLine("Write the day when you want this application to run automatically [1:Monday...7:Sunday]");
                do{
                    string input = Console.ReadLine();
                    if(!(    int.TryParse(input, out day)
                         && (day >=1 && day <=7))) {
                        day = -1;
                        tryAgain = true;
                        Console.WriteLine("Invalid input, try again.");
                    } else {
                        tryAgain = false;
                    }
                }while(tryAgain);
                
                tryAgain = true;
                Console.WriteLine("Write the hour when you want this application to run automatically [0-23]");
                do{
                    string input = Console.ReadLine();
                    if(!(    int.TryParse(input, out hour)
                         && (hour >=0 && hour <=23))) {
                        hour = -1;
                        tryAgain = true;
                        Console.WriteLine("Invalid input, try again.");
                    } else {
                        tryAgain = false;
                    }
                }while(tryAgain);

                using (TaskService ts = new TaskService()) {
                    TaskDefinition td = ts.NewTask();
                    td.RegistrationInfo.Description = $"Update hosts file with the list specified in {Directory.GetCurrentDirectory()}\\blocklist.txt";
                    td.RegistrationInfo.Author      = "HostsAutoUpdater.exe";
                    td.RegistrationInfo.Date        = DateTime.Now;
                    td.Principal.RunLevel           = TaskRunLevel.Highest;

                    WeeklyTrigger wt = new WeeklyTrigger();

                    wt.DaysOfWeek = (DaysOfTheWeek)Enum.Parse(typeof(DaysOfTheWeek), (1<<(day%7)).ToString());
                    wt.WeeksInterval = 1;
                    wt.StartBoundary = DateTime.Today + TimeSpan.FromHours(hour);

                    td.Triggers.Add(wt);

                    td.Actions.Add(new ExecAction(Process.GetCurrentProcess().MainModule.FileName,"",Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName)));

                    TaskService.Instance.RootFolder.RegisterTaskDefinition(TASK_NAME, td);

                    Console.WriteLine($"Scheduled task created with name {TASK_NAME}");
                }
            }

            Console.WriteLine("Now the allowlist file and blocklist file will be opened");
            Console.WriteLine("Fill allowlist.txt with the URLs you want to allow (one line for each URL)");
            Console.WriteLine("Fill blocklist.txt with URLs pointing to block lists in the hosts file format (ip url)");
            Console.WriteLine("Press any key to open them");
            Console.ReadKey();

            OpenWithDefaultProgram(pathAllowList);
            OpenWithDefaultProgram(pathBlockList);

            Console.WriteLine("After finished to update the lists press any key to start the update");
            Console.WriteLine("To restart this prompt delete the \".init\" file");
            Console.ReadKey();
            
            File.Create(".init");
        }

        public static void OpenWithDefaultProgram(string path) {
            using Process fileopener = new Process();

            fileopener.StartInfo.FileName = "explorer";
            fileopener.StartInfo.Arguments = "\"" + path + "\"";
            fileopener.Start();
        }
    }
}
