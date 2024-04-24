using System.Threading.Tasks;

namespace CloudInstallEngine.Models
{
    public interface IContainerHostedResource<RESOURCESCONTAINERTYPE>
    {
        RESOURCESCONTAINERTYPE Container { get; set; }
    }

    public interface IReturnResultTask<TASKRESULTINGRESOURCE>
    {
        Task<TASKRESULTINGRESOURCE> ExecuteTaskReturnResult(object contextArg);
    }

    public class CognitiveServicesInfo
    {
        public string Key { get; set; }
        public string Endpoint { get; set; }
    }

}
