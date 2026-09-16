#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UniTestify.Tests.EditMode")]
[assembly: InternalsVisibleTo("UniTestify.Tests.PlayMode")]
[assembly: InternalsVisibleTo("UniTestify.Editor")]
[assembly: InternalsVisibleTo("UniTestify.Pipeline")]
#endif
