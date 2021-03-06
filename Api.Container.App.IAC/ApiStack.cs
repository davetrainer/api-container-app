using Pulumi;
using Pulumi.AzureNative.ContainerRegistry;
using Pulumi.AzureNative.OperationalInsights;
using Pulumi.AzureNative.OperationalInsights.Inputs;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Web.V20210301;
using Pulumi.AzureNative.Web.V20210301.Inputs;
using Pulumi.Docker;
using ContainerArgs = Pulumi.AzureNative.Web.V20210301.Inputs.ContainerArgs;
using SecretArgs = Pulumi.AzureNative.Web.V20210301.Inputs.SecretArgs;


public class ApiStack : Stack
{
    public ApiStack()
    {
        var config = new Pulumi.Config();

        var resourceGroupName = config.Require("resourcegroupname");

        // Create an Azure Resource Group
        var resourceGroup = new ResourceGroup(resourceGroupName);

        var workspace = new Workspace("loganalytics", new WorkspaceArgs
        {
            ResourceGroupName = resourceGroup.Name,
            Sku = new WorkspaceSkuArgs { Name = "PerGB2018" },
            RetentionInDays = 30,
        });

        var workspaceSharedKeys = Output.Tuple(resourceGroup.Name, workspace.Name).Apply(items =>
            GetSharedKeys.InvokeAsync(new GetSharedKeysArgs
            {
                ResourceGroupName = items.Item1,
                WorkspaceName = items.Item2,
            }));

        var kubeEnv = new KubeEnvironment("env", new KubeEnvironmentArgs
        {
            ResourceGroupName = resourceGroup.Name,
            Type = "Managed",
            AppLogsConfiguration = new AppLogsConfigurationArgs
            {
                Destination = "log-analytics",
                LogAnalyticsConfiguration = new LogAnalyticsConfigurationArgs
                {
                    CustomerId = workspace.CustomerId,
                    SharedKey = workspaceSharedKeys.Apply(r => r.PrimarySharedKey)
                }
            }
        });

        var registry = new Registry("registry", new RegistryArgs
        {
            ResourceGroupName = resourceGroup.Name,
            Sku = new Pulumi.AzureNative.ContainerRegistry.Inputs.SkuArgs { Name = "Basic" },
            AdminUserEnabled = true
        });

        var credentials = Output.Tuple(resourceGroup.Name, registry.Name)
                                .Apply(items =>
            ListRegistryCredentials.InvokeAsync(new ListRegistryCredentialsArgs
            {
                ResourceGroupName = items.Item1,
                RegistryName = items.Item2
            }));

        var adminUsername = credentials.Apply(c => c.Username);
        var adminPassword = credentials.Apply(c => c.Passwords[0].Value);

        var customImage = "api.container.app.api";
        var myImage = new Image(customImage, new ImageArgs
        {
            ImageName = Output.Format($"{registry.LoginServer}/{customImage}:v1.0.0"),
            Build = new DockerBuild { Context = $"../" },
            Registry = new ImageRegistry
            {
                Server = registry.LoginServer,
                Username = adminUsername,
                Password = adminPassword
            }
        });

        var containerApp = new ContainerApp("app", new ContainerAppArgs
        {
            ResourceGroupName = resourceGroup.Name,
            KubeEnvironmentId = kubeEnv.Id,
            Configuration = new ConfigurationArgs
            {
                Ingress = new IngressArgs
                {
                    External = true,
                    TargetPort = 80
                },
                Registries = {
            new RegistryCredentialsArgs
            {
                Server = registry.LoginServer,
                Username = adminUsername,
                PasswordSecretRef = "pwd"
            }
        },
                Secrets = {
            new SecretArgs
            {
                Name = "pwd",
                Value = adminPassword
            }
        },
            },
            Template = new TemplateArgs
            {
                Containers = {
            new ContainerArgs
            {
                Name = "myapp",
                Image = myImage.ImageName
            }
        }
            }
        });

        Url = Output.Format($"https://{containerApp.Configuration.Apply(c => c.Ingress).Apply(i => i.Fqdn)}");
    }

    [Output]
    public Output<string> Url { get; set; }
}
