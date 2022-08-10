    using System;

namespace MyThreadPoolConsole 
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var threadPool = new MyThreadPool();

            threadPool.QueueUserWorkItem(() =>
            {
                ExecuteMethod1();
            },
            (ts) =>
            {
                Console.WriteLine($"Done 1 with status {ts.Success}");
            });

            threadPool.QueueUserWorkItem(() =>
            {
                ExecuteMethod2();
            },
            (ts) =>
            {
                Console.WriteLine($"Done 2 with status {ts.Success}");
            });

        }

        private static void ExecuteMethod1()
        {
            Console.WriteLine("ExecuteMethod1");
        }

        private static void ExecuteMethod2()
        {
            Console.WriteLine("ExecuteMethod2");
        }
    }



    public delegate void UserTask();
    public class ClientHandle
    {
        public Guid ID;
        public bool IsSimpleTask = false;
        public WaitCallback Finished;
    }
    public class TaskStatus
    {
        public bool Success = true;
        public Exception InnerException = null;
    }


    public class MyThreadPool
    {
        private const int MAX = 8;
        private const int MIN = 3; 
        private const int MIN_WAIT = 10;
        private const int MAX_WAIT = 15000;
        private const int CLEANUP_INTERVAL = 60000;
        private const int SCHEDULING_INTERVAL = 10; 

        private static readonly MyThreadPool _instance = new MyThreadPool();

        public MyThreadPool()
        {
            InitializeThreadPool();
        }

        public static MyThreadPool Instance
        {
            get
            {
                return _instance;
            }
        }

        enum TaskState
        {
            Notstarted,
            Processing,
            Finished,
            Aborted
        }
        class TaskHandle 
        {
            public ClientHandle Token;  
            public UserTask task; 
            public Action<TaskStatus> callback; 
        }

        class TaskItem 
        {
            public TaskHandle taskHandle;
            public Thread handler;
            public TaskState taskState = TaskState.Notstarted;
            public DateTime startTime = DateTime.MaxValue;
        }

        private Queue<TaskHandle> ReadyQueue = null;
        private List<TaskItem> Pool = null;
        private Thread taskScheduler = null;

        private void InitializeThreadPool()
        {
            ReadyQueue = new Queue<TaskHandle>();
            Pool = new List<TaskItem>();

            InitPoolWithMinCapacity(); 

            DateTime LastCleanup = DateTime.Now;

            taskScheduler = new Thread(() =>
            {
                do
                {
                    while (ReadyQueue.Count > 0 && ReadyQueue.Peek().task == null)
                        ReadyQueue.Dequeue();

                    int itemCount = ReadyQueue.Count;
                    for (int i = 0; i < itemCount; i++)
                    {
                        TaskHandle readyItem = ReadyQueue.Peek();
                        bool Added = false;

                        foreach (TaskItem ti in Pool)
                        {
                            if (ti.taskState == TaskState.Finished)
                            {
                                ti.taskHandle = readyItem;
                                ti.taskState = TaskState.Notstarted;
                                Added = true;
                                ReadyQueue.Dequeue();
                                break;
                            }
                        }
                        if (!Added && Pool.Count < MAX)
                        {
                            TaskItem ti = new TaskItem() { taskState = TaskState.Notstarted };
                            ti.taskHandle = readyItem;
                            AddTaskToPool(ti);
                            Added = true;
                            ReadyQueue.Dequeue();
                        }
                        if (!Added) break;
                    }
                    if ((DateTime.Now - LastCleanup) > TimeSpan.FromMilliseconds(CLEANUP_INTERVAL))
                     {
                        CleanupPool();
                        LastCleanup = DateTime.Now;
                    }
                    else
                    {
                        Thread.Yield();
                        Thread.Sleep(SCHEDULING_INTERVAL);
                    }
                } while (true);
            });
            taskScheduler.Priority = ThreadPriority.AboveNormal;
            taskScheduler.Start();
        }

        private void InitPoolWithMinCapacity()
        {
            for (int i = 0; i <= MIN; i++)
            {
                TaskItem ti = new TaskItem() { taskState = TaskState.Notstarted };
                ti.taskHandle = new TaskHandle() { task = () => { } };
                ti.taskHandle.callback = (taskStatus) => { };
                ti.taskHandle.Token = new ClientHandle() { ID = Guid.NewGuid() };
                AddTaskToPool(ti);
            }
        }

        private void AddTaskToPool(TaskItem taskItem)
        {
            taskItem.handler = new Thread(() =>
            {
                do
                {
                    bool Enter = false;

                    if (taskItem.taskState == TaskState.Aborted) break;

                    if (taskItem.taskState == TaskState.Notstarted)
                    {
                        taskItem.taskState = TaskState.Processing;
                        taskItem.startTime = DateTime.Now;
                        Enter = true;
                    }
                    if (Enter)
                    {
                        TaskStatus taskStatus = new TaskStatus();
                        try
                        {
                            taskItem.taskHandle.task.Invoke();
                            taskStatus.Success = true;
                        }
                        catch (Exception ex)
                        {
                            taskStatus.Success = false;
                            taskStatus.InnerException = ex;
                        }
                        if (taskItem.taskHandle.callback != null && taskItem.taskState != TaskState.Aborted)
                        {
                            try
                            {
                                taskItem.taskState = TaskState.Finished;
                                taskItem.startTime = DateTime.MaxValue;

                                taskItem.taskHandle.callback(taskStatus);
                            }
                            catch
                            {

                            }
                        }
                    }
                    Thread.Yield(); Thread.Sleep(MIN_WAIT);
                } while (true); 
            });
            taskItem.handler.Start();
            Pool.Add(taskItem);
        }

        private object syncLock = new object();
        private object criticalLock = new object();

        public ClientHandle QueueUserWorkItem(UserTask task, Action<TaskStatus> callback)
        {
            TaskHandle th = new TaskHandle()
            {
                task = task,
                Token = new ClientHandle()
                {
                    ID = Guid.NewGuid()
                },
                callback = callback
            };
            ReadyQueue.Enqueue(th);
            return th.Token;
        }

        public static void CancelUserTask(ClientHandle clientToken)
        {
            lock (Instance.syncLock)
            {
                var thandle = Instance.ReadyQueue.FirstOrDefault((th) => th.Token.ID == clientToken.ID);
                if (thandle != null)
                {
                    thandle.task = null;
                    thandle.callback = null;
                    thandle.Token = null;
                }
                else 
                {
                    int itemCount = Instance.ReadyQueue.Count;
                    TaskItem taskItem = null;
                    lock (Instance.criticalLock)
                    {
                        taskItem = Instance.Pool.FirstOrDefault(task => task.taskHandle.Token.ID == clientToken.ID);
                    }
                    if (taskItem != null)
                    {
                        lock (taskItem)
                        {
                            if (taskItem.taskState != TaskState.Finished)
                            {
                                taskItem.taskState = TaskState.Aborted;
                                taskItem.taskHandle.callback = null;
                            }
                            if (taskItem.taskState == TaskState.Aborted) 
                            {
                                try
                                {
                                    taskItem.handler.Priority = ThreadPriority.BelowNormal;
                                    taskItem.handler.IsBackground = true;
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
        }


        private void CleanupPool()
        {
            List<TaskItem> filteredTask = null;
            lock (criticalLock) 
            {
                filteredTask = Pool.Where(ti => ti.taskHandle.Token.IsSimpleTask == true &&
                  (DateTime.Now - ti.startTime) > TimeSpan.FromMilliseconds(MAX_WAIT)).ToList();
            }
            foreach (var taskItem in filteredTask)
            {
                CancelUserTask(taskItem.taskHandle.Token);
            }
            lock (criticalLock)
            {
                filteredTask = Pool.Where(ti => ti.taskState == TaskState.Aborted).ToList();
                foreach (var taskItem in filteredTask) 
                {
                    try
                    {
                        taskItem.handler.Abort(); 
                        taskItem.handler.Priority = ThreadPriority.Lowest;
                        taskItem.handler.IsBackground = true;
                    }
                    catch { }
                    Pool.Remove(taskItem);
                }
                int total = Pool.Count;
                if (total >= MIN) 
                {
                    filteredTask = Pool.Where(ti => ti.taskState == TaskState.Finished).ToList();
                    foreach (var taskItem in filteredTask)
                    {
                        taskItem.handler.Priority = ThreadPriority.AboveNormal;
                        taskItem.taskState = TaskState.Aborted;
                        Pool.Remove(taskItem);
                        total--;
                        if (total == MIN) break;
                    }
                }
                while (Pool.Count < MIN)
                {
                    TaskItem ti = new TaskItem() { taskState = TaskState.Notstarted };
                    ti.taskHandle = new TaskHandle() { task = () => { } };
                    ti.taskHandle.Token = new ClientHandle() { ID = Guid.NewGuid() };
                    ti.taskHandle.callback = (taskStatus) => { };
                    AddTaskToPool(ti);
                }
            }
        }

    }
}
