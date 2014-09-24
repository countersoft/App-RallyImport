using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web.Mvc;
using Countersoft.Gemini.Commons.Dto;
using Countersoft.Gemini.Commons.Entity;
using Countersoft.Gemini.Commons.Meta;
using Countersoft.Gemini.Extensibility.Apps;
using Countersoft.Gemini.Infrastructure.Apps;
using Countersoft.Foundation.Commons.Extensions;
using Rally.RestApi;

namespace RallyImport
{
    [AppType(AppTypeEnum.Config),
    AppGuid(Constants.AppGuid),
    AppControlGuid(Constants.ControlGuid),
    AppAuthor("Countersoft"),
    AppKey("RallyImport"),
    AppName("Rally Import"),
    AppDescription("Import stories, defects and tasks from Rally"),
    AppRequiresConfigScreen(true)]
    [OutputCache(Duration = 0, NoStore = true, Location = System.Web.UI.OutputCacheLocation.None)]
    public class Import : BaseAppController
    {
        private int MAX_RESULTS = Int32.MaxValue;
        private int MAX_META_RESULTS = Int32.MaxValue;
        private Dictionary<string, int> _projects = new Dictionary<string, int>();
        private Dictionary<string, int> _versions = new Dictionary<string, int>();
        private Dictionary<string, int> _users = new Dictionary<string, int>();
        private Dictionary<string, int> _issues = new Dictionary<string, int>();
        private List<IssueStatusDto> _geminiStatuses;
        private List<IssueSeverityDto> _geminiSeverities;
        private List<IssuePriorityDto> _geminiPriorities;
        private List<IssueResolution> _geminiResolutions;
        private List<Pair<string, dynamic>> _rallyUsers = new List<Pair<string, dynamic>>();
        private List<ProjectDto> _geminiProjects = new List<ProjectDto>();
        private int _storyId;
        private int _defectId;
        private int _taskId;
        private int _timeType;
        private int _cfRallyId;
        private int _cfBlocked;
        private int _cfBlockedReason;
        private Dictionary<string, int> _customFields = new Dictionary<string, int>();
        private static int _storiesImported;
        private static int _defectsImported;
        private static int _tasksImported;
        private static bool _finished = true;
        private static bool _inProgress = false;
        private static Exception _exception;
        private static DateTime _start;
        private static DateTime _end;


        private Dictionary<string, string> _projectsToImport;

        public override WidgetResult Caption(Countersoft.Gemini.Commons.Dto.IssueDto issue = null)
        {
            WidgetResult result = new WidgetResult();

            result.Success = true;

            result.Markup.Html = "Rally Import";

            return result;
        }

        public override WidgetResult Show(Countersoft.Gemini.Commons.Dto.IssueDto issue = null)
        {
            WidgetResult result = new WidgetResult();

            result.Markup.Html = AppDescription;

            result.Success = true;

            return result;
        }

        public override WidgetResult Configuration()
        {
            WidgetResult result = new WidgetResult();

            result.Markup =  _inProgress ? new WidgetMarkup(GetStatus()) : new WidgetMarkup("views\\Credentials.cshtml", string.Empty);

            result.Success = true;

            return result;
        }

        [AppUrl("connect")]
        public ActionResult Connect(string url, string username, string password)
        {
            try
            {
                RallyRestApi restApi = new RallyRestApi(username, password, url);
                Request query = new Request("Project");
                query.Limit = Int32.MaxValue;

                var result = restApi.Query(query);
                MappingModel model = new MappingModel();
                model.Username = username;
                model.Password = password;
                model.Url = url;
                List<RallyProject> projects = new List<RallyProject>();
                model.Templates = new SelectList(ProjectTemplateManager.GetAll(), "Id", "Name");
                projects.Add(new RallyProject() { Id = "0", Name = "All" });

                foreach (var project in result.Results)
                {
                    if (project.Name.Equals("All Projects", System.StringComparison.InvariantCultureIgnoreCase)) continue;

                    RallyProject rallyProject = new RallyProject();
                    rallyProject.Name = project.Name;
                    rallyProject.Id = project._refObjectUUID;

                    projects.Add(rallyProject);
                }

                model.Projects = new MultiSelectList(projects, "Id", "Name");

                return JsonSuccess(new { Html = RenderPartialViewToString(this, AppManager.Instance.GetAppUrl(AppGuid, "views/Mappings.cshtml"), model) });
            }
            catch (Exception ex)
            {
                LogException(ex);
                return JsonError(ex);
            }
        }

        private string GetStatus()
        {
            var model = new StatusModel();
            model.Messages.Add(string.Format("{0:n0} Stories imported", _storiesImported));
            model.Messages.Add(string.Format("{0:n0} Defects imported", _defectsImported));
            model.Messages.Add(string.Format("{0:n0} Tasks imported", _tasksImported));
            if (_finished)
            {
                model.Status = string.Format("Import finished at {0}", _end.ToLocal(CurrentUser.TimeZone));

                if (_exception == null)
                {
                    model.Messages.Add(string.Format("Completed successfully - {0:n0} Items imported.", (_storiesImported + _defectsImported + _tasksImported)));
                }
                else
                {
                    model.Messages.Add(string.Format("Failed to import- {0:n0} Items imported.", (_storiesImported + _defectsImported + _tasksImported)));
                    model.Messages.Add(_exception.ToString());
                }
                _inProgress = false;
                //_exception = null;
            }
            else
            {
                model.Status = string.Format("Import started at {0}, still running", _start.ToLocal(CurrentUser.TimeZone));
                model.Messages.Add(string.Format("{0:n0} total items imported.", (_storiesImported + _defectsImported + _tasksImported)));
            }

            

            return RenderPartialViewToString(this, AppManager.Instance.GetAppUrl(AppGuid, "views/Status.cshtml"), model);
        }

        [AppUrl("status")]
        public ActionResult Status()
        {
            return JsonSuccess(new { Html = GetStatus() });
        }

        [AppUrl("import")]
        public ActionResult DoImport(string url, string username, string password, string template, List<string> project, string customFields)
        {
            try
            {
                _inProgress = true;
                _finished = false;
                _storiesImported = 0;
                _defectsImported = 0;
                _tasksImported = 0;
                _exception = null;
                _start = DateTime.UtcNow;

                System.Threading.Tasks.Task.Factory.StartNew(() =>
                {
                    try
                    {
                        RallyRestApi restApi = new RallyRestApi(username, password, url);

                        _projectsToImport = new Dictionary<string, string>(project.Count);
                        project.ForEach(p => _projectsToImport.Add(p, p));

                        var story = MetaManager.TypeGet(template.ToInt(), "Story");
                        if (story == null)
                        {
                            _storyId = MetaManager.TypeGetAll(template.ToInt())[0].Entity.Id;
                        }
                        else
                        {
                            _storyId = story.Entity.Id;
                        }

                        var defect = MetaManager.TypeGet(template.ToInt(), "Defect");
                        if (defect == null)
                        {
                            defect = MetaManager.TypeGet(template.ToInt(), "Bug");
                            if (defect == null)
                            {
                                _defectId = MetaManager.TypeGetAll(template.ToInt())[0].Entity.Id;
                            }
                            else
                            {
                                _defectId = defect.Entity.Id;
                            }
                        }
                        else
                        {
                            _defectId = defect.Entity.Id;
                        }

                        var task = MetaManager.TypeGet(template.ToInt(), "Task");
                        if (task == null)
                        {
                            _taskId = MetaManager.TypeGetAll(template.ToInt())[0].Entity.Id;
                        }
                        else
                        {
                            _taskId = task.Entity.Id;
                        }
                        _timeType = MetaManager.TimeTypeGetAll(template.ToInt())[0].Id;


                        var templateCustomFields = CustomFieldManager.GetAll().FindAll(c => c.Entity.TemplateId == template.ToInt());
                        var rcf = templateCustomFields.Find(c => c.Entity.Name.Equals("RallyID", StringComparison.InvariantCultureIgnoreCase));
                        if (rcf != null)
                        {
                            _cfRallyId = rcf.Entity.Id;
                        }
                        else
                        {
                            _cfRallyId = 0;
                        }

                        rcf = templateCustomFields.Find(c => c.Entity.Name.Equals("Blocked", StringComparison.InvariantCultureIgnoreCase));
                        if (rcf != null)
                        {
                            _cfBlocked = rcf.Entity.Id;
                        }
                        else
                        {
                            _cfBlocked = 0;
                        }

                        rcf = templateCustomFields.Find(c => c.Entity.Name.Equals("BlockedReason", StringComparison.InvariantCultureIgnoreCase));
                        if (rcf != null)
                        {
                            _cfBlockedReason = rcf.Entity.Id;
                        }
                        else
                        {
                            _cfBlockedReason = 0;
                        }

                        if (customFields.HasValue())
                        {
                            var cfs = customFields.Split(',');
                            foreach (var cf in cfs)
                            {
                                if (cf.IsEmpty()) continue;
                                var map = templateCustomFields.Find(c => c.Entity.Name.Equals(cf, StringComparison.InvariantCultureIgnoreCase));
                                if (map != null)
                                {
                                    _customFields.Add(string.Concat("c_", cf), map.Entity.Id);
                                }
                            }
                        }

                        GetMeta();
                        GetUsers(restApi);
                        GetProjects(restApi);

                        GetReleases(restApi);
                        GetIterations(restApi);

                        GetStories(restApi);
                        GetDefects(restApi);
                        GetTasks(restApi);
                    }
                    catch (Exception ex)
                    {
                        LogException(ex);
                        _exception = ex;
                    }
                    finally
                    {
                        _end = DateTime.UtcNow;
                        _finished = true;
                    }
                });

                return Status();
            }
            catch (Exception ex)
            {
                LogException(ex);
                return JsonError(ex);
            }
        }

        #region Actual Import

        private static bool HasProperty(dynamic obj, string property)
        {
            return obj.HasMember(property);
        }

        private static int GetProperty(dynamic obj, string property)
        {
            if (!HasProperty(obj, property))
            {
                return 0;
            }
            else
            {
                object o = obj[property];
                if (o == null) return 0;
                return System.Convert.ToInt32(o);
            }
        }

        private static T GetProperty<T>(dynamic obj, string property)
        {
            if (!HasProperty(obj, property))
            {
                return default(T);
            }
            else
            {
                if (typeof(T) == typeof(int))
                {
                    object o = obj[property];
                    if (o == null) return default(T);
                    return (T)o;
                }

                return (T)obj[property];
            }
        }
                

        private void GetMeta()
        {
            _geminiStatuses = MetaManager.StatusGetAll();
            _geminiSeverities = MetaManager.SeverityGetAll();
            _geminiPriorities = MetaManager.PriorityGetAll();
            _geminiResolutions = MetaManager.ResolutionGetAll();
        }

        private int GetStatus(string name, int projectId)
        {
            var project = _geminiProjects.Find(p => p.Entity.Id == projectId);
            if (project == null) return 0;
            var status = _geminiStatuses.Find(s => s.Entity.TemplateId == project.Entity.TemplateId && s.Entity.Label.Equals(name, System.StringComparison.InvariantCultureIgnoreCase));
            if (status == null) return 0;
            return status.Entity.Id;
        }

        private int GetPriority(string name, int projectId)
        {
            var project = _geminiProjects.Find(p => p.Entity.Id == projectId);
            if (project == null) return 0;
            var dto = _geminiPriorities.Find(s => s.Entity.TemplateId == project.Entity.TemplateId && s.Entity.Label.Equals(name, System.StringComparison.InvariantCultureIgnoreCase));
            if (dto == null) return 0;
            return dto.Entity.Id;
        }

        private int GetSeverity(string name, int projectId)
        {
            var project = _geminiProjects.Find(p => p.Entity.Id == projectId);
            if (project == null) return 0;
            var dto = _geminiSeverities.Find(s => s.Entity.TemplateId == project.Entity.TemplateId && s.Entity.Label.Equals(name, System.StringComparison.InvariantCultureIgnoreCase));
            if (dto == null) return 0;
            return dto.Entity.Id;
        }

        private int GetResolution(string name, int projectId)
        {
            var project = _geminiProjects.Find(p => p.Entity.Id == projectId);
            if (project == null) return 0;
            var dto = _geminiResolutions.Find(s => s.TemplateId == project.Entity.TemplateId && s.Label.Equals(name, System.StringComparison.InvariantCultureIgnoreCase));
            if (dto == null) return 0;
            return dto.Id;
        }

        private void GetUsers(RallyRestApi restApi)
        {
            Request query = new Request("User");
            query.Limit = MAX_META_RESULTS;
            var result = restApi.Query(query);
            var geminiUsers = Cache.Users.FindAll(u => u.Active);
            foreach (var user in result.Results)
            {
                var gu = geminiUsers.Find(u => u.Email.Equals(user.EmailAddress, System.StringComparison.InvariantCultureIgnoreCase));
                if (gu != null)
                {
                    _users.Add(user._refObjectUUID, gu.Id);
                }
                else
                {
                    _users.Add(user._refObjectUUID, -1);
                }
                _rallyUsers.Add(new Pair<string, dynamic>(user._refObjectName, user));
            }
        }

        private void GetProjects(RallyRestApi restApi)
        {
            Request query = new Request("Project");
            query.Limit = MAX_META_RESULTS;
            var result = restApi.Query(query);
            foreach (var project in result.Results)
            {
                if (project.Name.Equals("All Projects", System.StringComparison.InvariantCultureIgnoreCase)) continue;
                if(!_projectsToImport.ContainsKey(project._refObjectUUID) && !_projectsToImport.ContainsKey("0")) continue;

                Project geminiProject = new Project();
                geminiProject.Code = Countersoft.Foundation.Commons.Extensions.ToExtensions.ToMax(project.Name, 10);
                geminiProject.Description = Countersoft.Foundation.Utility.Helpers.HtmlHelper.ConvertHtmlToText2(project.Description);
                geminiProject.Name = project.Name;
                geminiProject.TemplateId = 10;
                geminiProject.LeadId = -2;
                var dto = ProjectManager.Create(geminiProject);
                _geminiProjects.Add(dto);
                _projects.Add(project._refObjectUUID, dto.Entity.Id);
                /*
                var _rallyAPIMajor = project._rallyAPIMajor;
                var _rallyAPIMinor = project._rallyAPIMinor;
                var _ref = project._ref;
                var _refObjectUUID = project._refObjectUUID;
                var _objectVersion = project._objectVersion;
                var _refObjectName = project._refObjectName;
                var CreationDate = project.CreationDate;
                var _CreatedAt = project._CreatedAt;
                var ObjectID = project.ObjectID;
                var VersionId = project.VersionId;
                var Subscription = project.Subscription;
                var Workspace = project.Workspace;
                var BuildDefinitions = project.BuildDefinitions;
                var Children = project.Children;
                var Description = project.Description;
                var Editors = project.Editors;
                var Iterations = project.Iterations;
                var Name = project.Name;
                var Notes = project.Notes;
                var Owner = project.Owner;
                var Parent = project.Parent;
                var Releases = project.Releases;
                var SchemaVersion = project.SchemaVersion;
                var State = project.State;
                var TeamMembers = project.TeamMembers;
                var _type = project._type;*/
            }
        }

        private void GetReleases(RallyRestApi restApi)
        {
            Request query = new Request("Release");
            query.Limit = MAX_META_RESULTS;
            var result = restApi.Query(query);
            foreach (var version in result.Results)
            {
                if (!_projectsToImport.ContainsKey(version.Project._refObjectUUID) && !_projectsToImport.ContainsKey("0")) continue;

                Countersoft.Gemini.Commons.Entity.Version geminiVersion = new Countersoft.Gemini.Commons.Entity.Version();
                geminiVersion.Label = version.Notes;
                geminiVersion.Name = version.Name;
                geminiVersion.ProjectId = _projects[version.Project._refObjectUUID];
                geminiVersion.Released = version.ReleaseDate != null;
                geminiVersion.ReleaseDate = System.DateTime.Parse(version.ReleaseDate);
                geminiVersion.StartDate = System.DateTime.Parse(version.ReleaseStartDate);
                var dto = VersionManager.Create(geminiVersion);
                _versions.Add(version._refObjectUUID, dto.Entity.Id);
                /*                var _rallyAPIMajor = version._rallyAPIMajor;
                                var _rallyAPIMinor = version._rallyAPIMinor;
                                var _ref = version._ref;
                                var _refObjectUUID = version._refObjectUUID;
                                var _objectVersion = version._objectVersion;
                                var _refObjectName = version._refObjectName;
                                var CreationDate = version.CreationDate;
                                var _CreatedAt = version._CreatedAt;
                                var ObjectID = version.ObjectID;
                                var VersionId = version.VersionId;
                                var Subscription = version.Subscription;
                                var Workspace = version.Workspace;
                                var GrossEstimateConversionRatio = version.GrossEstimateConversionRatio;
                                var Name = version.Name;
                                var Notes = version.Notes;
                                var PlannedVelocity = version.PlannedVelocity;
                                var Project = version.Project;
                                var ReleaseDate = version.ReleaseDate;
                                var ReleaseStartDate = version.ReleaseStartDate;
                                var RevisionHistory = version.RevisionHistory;
                                var State = version.State;
                                var Theme = version.Theme;
                                var _type = version._type;*/
            }
        }

        private void GetIterations(RallyRestApi restApi)
        {
            Request query = new Request("Iteration");
            query.Limit = MAX_META_RESULTS;
            var result = restApi.Query(query);
            foreach (var version in result.Results)
            {
                if (!_projectsToImport.ContainsKey(version.Project._refObjectUUID) && !_projectsToImport.ContainsKey("0")) continue;

                Countersoft.Gemini.Commons.Entity.Version geminiVersion = new Countersoft.Gemini.Commons.Entity.Version();
                geminiVersion.Label = version.Notes;
                geminiVersion.Name = version.Name;
                geminiVersion.ProjectId = _projects[version.Project._refObjectUUID];
                geminiVersion.Released = version.EndDate != null;
                geminiVersion.ReleaseDate = System.DateTime.Parse(version.EndDate);
                geminiVersion.StartDate = System.DateTime.Parse(version.StartDate);
                var dto = VersionManager.Create(geminiVersion);
                _versions.Add(version._refObjectUUID, dto.Entity.Id);

                /*var _rallyAPIMajor = version._rallyAPIMajor;
                var _rallyAPIMinor = version._rallyAPIMinor;
                var _ref = version._ref;
                var _refObjectUUID = version._refObjectUUID;
                var _objectVersion = version._objectVersion;
                var _refObjectName = version._refObjectName;
                var CreationDate = version.CreationDate;
                var _CreatedAt = version._CreatedAt;
                var ObjectID = version.ObjectID;
                var VersionId = version.VersionId;
                var Subscription = version.Subscription;
                var Workspace = version.Workspace;
                var EndDate = version.EndDate;
                var Name = version.Name;
                var Notes = version.Notes;
                var PlannedVelocity = version.PlannedVelocity;
                var Project = version.Project;
                var RevisionHistory = version.RevisionHistory;
                var StartDate = version.StartDate;
                var State = version.State;
                var Theme = version.Theme;
                var UserIterationCapacities = version.UserIterationCapacities;
                var _type = version._type;*/
            }
        }

        private void GetStories(RallyRestApi restApi)
        {
            Request query = new Request("hierarchicalrequirement");
            query.Limit = MAX_RESULTS;
            var result = restApi.Query(query);
            foreach (var story in result.Results)
            {
                if (!_projectsToImport.ContainsKey(story.Project._refObjectUUID) && !_projectsToImport.ContainsKey("0")) continue;

                Issue issue = new Issue();

                //issue.ClosedDate;
                issue.Created = System.DateTime.Parse(story.CreationDate);
                issue.Description = story.Description;
                dynamic ver = GetProperty<dynamic>(story, "Iteration");
                if (ver != null)
                {
                    issue.FixedInVersionId = _versions[story.Iteration._refObjectUUID];
                }
                issue.Points = GetProperty(story, "PlanEstimate");// System.Convert.ToInt32(story.PlanEstimate);
                //issue.PriorityId;
                if (_projects.ContainsKey(story.Project._refObjectUUID))
                {
                    issue.ProjectId = _projects[story.Project._refObjectUUID];
                }
                else
                {
                    issue.ProjectId = _projects.First().Value;
                }

                //issue.AddResource();
                //issue.ReportedBy;
                //issue.ResolutionId;
                //issue.ResolvedDate
                //issue.SeverityId
                issue.StatusId = GetStatus((ver == null ? "In Backlog" : story.ScheduleState), issue.ProjectId);
                var owner = GetProperty<dynamic>(story, "Owner");
                if (owner != null)
                {
                    if (_users.ContainsKey(owner._refObjectUUID))
                    {
                        issue.AddResource(_users[owner._refObjectUUID]);
                    }
                }
                issue.Title = story.Name;
                issue.TypeId = _storyId;// 55; // Story

                var attachments = story.Attachments;
                if (attachments.Count > 0)
                {
                    var attachmentQuery = new Request(attachments);
                    var attachmentsData = restApi.Query(attachmentQuery);
                    foreach (var attachment in attachmentsData.Results)
                    {
                        var content = restApi.GetByReference(attachment._ref, "Content");
                        for (int i = 0; i < 3; i++)
                        {
                            try
                            {
                                var content2 = restApi.GetByReference(content.Content._ref, "Content");
                                IssueAttachment issueAttachment = new IssueAttachment();
                                issueAttachment.Content = System.Convert.FromBase64String(content2.Content);
                                issueAttachment.ContentType = attachment.ContentType;
                                issueAttachment.ContentLength = issueAttachment.Content.Length;
                                issueAttachment.Name = attachment.Name;
                                issue.Attachments.Add(issueAttachment);
                                break;
                            }
                            catch
                            {
                                System.Threading.Thread.Sleep(1000);
                            }
                        }
                    }
                }

                issue.Tag1 = story.FormattedID ?? string.Empty;

                var dto = IssueManager.Create(issue);
                _issues.Add(story._refObjectUUID, dto.Entity.Id);

                foreach (var c in _customFields)
                {
                    string cValue = GetProperty<string>(story, c.Key) ?? string.Empty;
                    CustomFieldData cf = new CustomFieldData();
                    cf.CustomFieldId = c.Value;
                    cf.Data = cValue;
                    cf.ProjectId = dto.Entity.ProjectId;
                    cf.IssueId = dto.Entity.Id;
                    cf.UserId = -2;
                    CustomFieldManager.Update(cf);
                }

                if (_cfRallyId != 0)
                {
                    CustomFieldData cf = new CustomFieldData();
                    cf.ProjectId = dto.Entity.ProjectId;
                    cf.IssueId = dto.Entity.Id;
                    cf.UserId = -2;
                    cf.CustomFieldId = _cfRallyId;
                    cf.Data = story.FormattedID;
                    CustomFieldManager.Update(cf);
                }

                string notes = GetProperty<string>(story, "Notes");
                if (notes.HasValue())
                {
                    IssueComment comment = new IssueComment();
                    comment.IssueId = dto.Entity.Id;
                    comment.UserId = -2;
                    comment.ProjectId = dto.Entity.ProjectId;
                    comment.Comment = notes;
                    IssueManager.IssueCommentCreate(comment);
                }

                if (_cfBlocked != 0)
                {
                    CustomFieldData cf = new CustomFieldData();
                    cf.ProjectId = dto.Entity.ProjectId;
                    cf.IssueId = dto.Entity.Id;
                    cf.UserId = -2;
                    cf.CustomFieldId = _cfBlocked;
                    cf.Data = story.Blocked ? "Y" : string.Empty;
                    CustomFieldManager.Update(cf);
                }

                if (_cfBlockedReason != 0)
                {
                    CustomFieldData cf = new CustomFieldData();
                    cf.ProjectId = dto.Entity.ProjectId;
                    cf.IssueId = dto.Entity.Id;
                    cf.UserId = -2;
                    cf.CustomFieldId = _cfBlockedReason;
                    cf.Data = GetProperty<string>(story, "BlockedReason") ?? string.Empty;
                    CustomFieldManager.Update(cf);
                }

                _storiesImported++;
                /*var Discussion = story.Discussion;
                if (Discussion.Count > 0)
                {
                    //System.Diagnostics.Debugger.Break();
                }
                
                xvar _rallyAPIMajor = story._rallyAPIMajor;
                xvar _rallyAPIMinor = story._rallyAPIMinor;
                xvar _ref = story._ref;
                xvar _refObjectUUID = story._refObjectUUID;
                xvar _objectVersion = story._objectVersion;
                xvar _refObjectName = story._refObjectName;
                xvar CreationDate = story.CreationDate;
                xvar _CreatedAt = story._CreatedAt;
                xvar ObjectID = story.ObjectID;
                xvar VersionId = story.VersionId;
                xvar Subscription = story.Subscription;
                xvar Workspace = story.Workspace;
                xvar Changesets = story.Changesets;
                xvar Discussion = story.Discussion;
                xvar Expedite = story.Expedite;
                xvar FormattedID = story.FormattedID;
                xvar LastUpdateDate = story.LastUpdateDate;
                xvar Name = story.Name;
                xvar Notes = story.Notes;
                xvar Owner = story.Owner;
                xvar Project = story.Project;
                xvar Ready = story.Ready;
                xvar RevisionHistory = story.RevisionHistory;
                xvar Tags = story.Tags;
                var Attachments = story.Attachments;
                xvar AcceptedDate = story.AcceptedDate;
                xvar Blocked = story.Blocked;
                xvar Children = story.Children;
                xvar DefectStatus = story.DefectStatus;
                xvar Defects = story.Defects;
                xvar DirectChildrenCount = story.DirectChildrenCount;
                xvar DragAndDropRank = story.DragAndDropRank;
                xvar HasParent = story.HasParent;
                xvar Iteration = story.Iteration;
                xvar PlanEstimate = story.PlanEstimate;
                xvar Predecessors = story.Predecessors;
                xvar Recycled = story.Recycled;
                xvar Release = story.Release;
                xvar ScheduleState = story.ScheduleState;
                xvar Successors = story.Successors;
                xvar TaskActualTotal = story.TaskActualTotal;
                xvar TaskEstimateTotal = story.TaskEstimateTotal;
                xvar TaskRemainingTotal = story.TaskRemainingTotal;
                xvar TaskStatus = story.TaskStatus;
                xvar Tasks = story.Tasks;
                xvar TestCaseStatus = story.TestCaseStatus;
                xvar TestCases = story.TestCases;
                var   = story.c_ExternalId;
                var c_ExternalIdOld = story.c_ExternalIdOld;
                xvar _type = story._type;*/
            }
        }


        private void GetDefects(RallyRestApi restApi)
        {
            Request query = new Request("Defect");
            query.Limit = MAX_RESULTS;
            var result = restApi.Query(query);
            foreach (var defect in result.Results)
            {
                if (!_projectsToImport.ContainsKey(defect.Project._refObjectUUID) && !_projectsToImport.ContainsKey("0")) continue;
                Issue issue = new Issue();
                //issue.Attachments;
                string closed = GetProperty<string>(defect, "ClosedDate");
                if (closed.HasValue())
                {
                    issue.ClosedDate = System.DateTime.Parse(closed);
                }
                issue.Created = System.DateTime.Parse(defect.CreationDate);
                issue.Description = defect.Description;
                dynamic ver = GetProperty<dynamic>(defect, "Iteration");
                if (ver != null)
                {
                    issue.FixedInVersionId = _versions[defect.Iteration._refObjectUUID];
                }
                issue.Points = GetProperty(defect, "PlanEstimate");// System.Convert.ToInt32(defect.PlanEstimate);
                //issue.PriorityId;
                if (_projects.ContainsKey(defect.Project._refObjectUUID))
                {
                    issue.ProjectId = _projects[defect.Project._refObjectUUID];
                }
                else
                {
                    issue.ProjectId = _projects.First().Value;
                }
                var submitted = GetProperty<dynamic>(defect, "SubmittedBy");
                if (submitted != null && _users.ContainsKey(submitted._refObjectUUID))
                {
                    issue.ReportedBy = _users[submitted._refObjectUUID];
                }
                issue.ResolutionId = GetResolution(defect.Resolution, issue.ProjectId);
                //issue.ResolvedDate
                issue.PriorityId = GetPriority(defect.Priority, issue.ProjectId);
                issue.SeverityId = GetSeverity(defect.Severity, issue.ProjectId);
                issue.StatusId = GetStatus((ver == null ? "In Backlog" : defect.State), issue.ProjectId);
                var owner = GetProperty<dynamic>(defect, "Owner");
                if (owner != null)
                {
                    if (_users.ContainsKey(owner._refObjectUUID))
                    {
                        issue.AddResource(_users[owner._refObjectUUID]);
                    }
                }
                issue.Title = defect.Name;
                issue.TypeId = _defectId; //59; // Bug

                var attachments = defect.Attachments;
                if (attachments.Count > 0)
                {
                    var attachmentQuery = new Request(attachments);
                    var attachmentsData = restApi.Query(attachmentQuery);
                    foreach (var attachment in attachmentsData.Results)
                    {
                        var content = restApi.GetByReference(attachment._ref, "Content");
                        for (int i = 0; i < 3; i++)
                        {
                            try
                            {
                                var content2 = restApi.GetByReference(content.Content._ref, "Content");
                                IssueAttachment issueAttachment = new IssueAttachment();
                                issueAttachment.Content = System.Convert.FromBase64String(content2.Content);
                                issueAttachment.ContentType = attachment.ContentType;
                                issueAttachment.ContentLength = issueAttachment.Content.Length;
                                issueAttachment.Name = attachment.Name;
                                issue.Attachments.Add(issueAttachment);
                                break;
                            }
                            catch
                            {
                                System.Threading.Thread.Sleep(1000);
                            }
                        }
                    }
                }

                issue.Tag1 = defect.FormattedID ?? string.Empty;

                var dto = IssueManager.Create(issue);
                _issues.Add(defect._refObjectUUID, dto.Entity.Id);

                foreach (var c in _customFields)
                {
                    string cValue = GetProperty<string>(defect, c.Key) ?? string.Empty;
                    CustomFieldData cf = new CustomFieldData();
                    cf.CustomFieldId = c.Value;
                    cf.Data = cValue;
                    cf.ProjectId = dto.Entity.ProjectId;
                    cf.IssueId = dto.Entity.Id;
                    cf.UserId = -2;
                    CustomFieldManager.Update(cf);
                }

                if (_cfRallyId != 0)
                {
                    CustomFieldData cf = new CustomFieldData();
                    cf.ProjectId = dto.Entity.ProjectId;
                    cf.IssueId = dto.Entity.Id;
                    cf.UserId = -2;
                    cf.CustomFieldId = _cfRallyId;
                    cf.Data = defect.FormattedID;
                    CustomFieldManager.Update(cf);
                }

                if (_cfBlocked != 0)
                {
                    CustomFieldData cf = new CustomFieldData();
                    cf.ProjectId = dto.Entity.ProjectId;
                    cf.IssueId = dto.Entity.Id;
                    cf.UserId = -2;
                    cf.CustomFieldId = _cfBlocked;
                    cf.Data = defect.Blocked ? "Y" : string.Empty;
                    CustomFieldManager.Update(cf);
                }

                if (_cfBlockedReason != 0)
                {
                    CustomFieldData cf = new CustomFieldData();
                    cf.ProjectId = dto.Entity.ProjectId;
                    cf.IssueId = dto.Entity.Id;
                    cf.UserId = -2;
                    cf.CustomFieldId = _cfBlockedReason;
                    cf.Data = GetProperty<string>(defect, "BlockedReason") ?? string.Empty;
                    CustomFieldManager.Update(cf);
                }

                string notes = GetProperty<string>(defect, "Notes");
                if (notes.HasValue())
                {
                    IssueComment comment = new IssueComment();
                    comment.IssueId = dto.Entity.Id;
                    comment.ProjectId = dto.Entity.ProjectId;
                    comment.UserId = -2;
                    comment.Comment = notes;
                    IssueManager.IssueCommentCreate(comment);
                }

                _defectsImported++;
                /*var Discussion = defect.Discussion;
                if (Discussion.Count > 0)
                {
                    //System.Diagnostics.Debugger.Break();
                }*/

                /*var Duplicates = defect.Duplicates;
                var Environment = defect.Environment;
                var FixedInBuild = defect.FixedInBuild;
                var FoundInBuild = defect.FoundInBuild;
                
                
                
                var TargetBuild = defect.TargetBuild;
                
                xvar _rallyAPIMajor = defect._rallyAPIMajor;
                xvar _rallyAPIMinor = defect._rallyAPIMinor;
                xvar _ref = defect._ref;
                xvar _refObjectUUID = defect._refObjectUUID;
                xvar _objectVersion = defect._objectVersion;
                xvar _refObjectName = defect._refObjectName;
                xvar CreationDate = defect.CreationDate;
                xvar _CreatedAt = defect._CreatedAt;
                xvar ObjectID = defect.ObjectID;
                xvar VersionId = defect.VersionId;
                xvar Subscription = defect.Subscription;
                xvar Workspace = defect.Workspace;
                xvar Changesets = defect.Changesets;
                xvar Description = defect.Description;
                var Discussion = defect.Discussion;
                var Expedite = defect.Expedite;
                xvar FormattedID = defect.FormattedID;
                var LastUpdateDate = defect.LastUpdateDate;
                var Name = defect.Name;
                var Notes = defect.Notes;
                var Owner = defect.Owner;
                var Project = defect.Project;
                var Ready = defect.Ready;
                var RevisionHistory = defect.RevisionHistory;
                var Tags = defect.Tags;
                var AffectsDoc = defect.AffectsDoc;
                var Attachments = defect.Attachments;
                var Blocked = defect.Blocked;
                var ClosedDate = defect.ClosedDate;
                var DefectSuites = defect.DefectSuites;
                var DragAndDropRank = defect.DragAndDropRank;
                var Duplicates = defect.Duplicates;
                var Environment = defect.Environment;
                var FixedInBuild = defect.FixedInBuild;
                var FoundInBuild = defect.FoundInBuild;
                var Iteration = defect.Iteration;
                var OpenedDate = defect.OpenedDate;
                var PlanEstimate = defect.PlanEstimate;
                var Priority = defect.Priority;
                var Recycled = defect.Recycled;
                var Release = defect.Release;
                var ReleaseNote = defect.ReleaseNote;
                var Resolution = defect.Resolution;
                var ScheduleState = defect.ScheduleState;
                var Severity = defect.Severity;
                var State = defect.State;
                var SubmittedBy = defect.SubmittedBy;
                var TargetBuild = defect.TargetBuild;
                var TargetDate = defect.TargetDate;
                var TaskActualTotal = defect.TaskActualTotal;
                var TaskEstimateTotal = defect.TaskEstimateTotal;
                var TaskRemainingTotal = defect.TaskRemainingTotal;
                var TaskStatus = defect.TaskStatus;
                var Tasks = defect.Tasks;
                var VerifiedInBuild = defect.VerifiedInBuild;
                var c_ExternalId = defect.c_ExternalId;
                var _type = defect._type;*/
            }
        }

        private void GetTasks(RallyRestApi restApi)
        {
            Countersoft.Gemini.Infrastructure.Managers.TimeTrackingManager timeManager = new Countersoft.Gemini.Infrastructure.Managers.TimeTrackingManager(IssueManager);
            Request query = new Request("Task");
            query.Limit = MAX_RESULTS;
            var result = restApi.Query(query);
            foreach (var task in result.Results)
            {
                if (!_projectsToImport.ContainsKey(task.Project._refObjectUUID) && !_projectsToImport.ContainsKey("0")) continue;

                Issue issue = new Issue();
                //issue.Attachments;
                issue.Created = System.DateTime.Parse(task.CreationDate);
                issue.Description = task.Description;
                dynamic ver = GetProperty<dynamic>(task, "Iteration");
                if (ver != null)
                {
                    issue.FixedInVersionId = _versions[task.Iteration._refObjectUUID];
                }

                decimal? estimated = GetProperty<decimal?>(task, "Estimate");

                if (estimated.HasValue)
                {
                    issue.EstimatedHours = (int)estimated.Value;
                    issue.EstimatedMinutes = (int)(estimated.Value - (int)estimated.Value) * 60;
                }
                //issue.PriorityId;
                if (_projects.ContainsKey(task.Project._refObjectUUID))
                {
                    issue.ProjectId = _projects[task.Project._refObjectUUID];
                }
                else
                {
                    issue.ProjectId = _projects.First().Value;
                }
                issue.StatusId = GetStatus((ver == null ? "In Backlog" : task.State), issue.ProjectId);
                var owner = GetProperty<dynamic>(task, "Owner");
                if (owner != null)
                {
                    if (_users.ContainsKey(owner._refObjectUUID))
                    {
                        issue.AddResource(_users[owner._refObjectUUID]);
                    }
                }
                issue.Title = task.Name;
                issue.TypeId = _taskId;// 57; // Task

                var workProduct = GetProperty<dynamic>(task, "WorkProduct");
                if (workProduct != null)
                {
                    issue.ParentIssueId = _issues[workProduct._refObjectUUID];
                }

                var attachments = task.Attachments;
                if (attachments.Count > 0)
                {
                    var attachmentQuery = new Request(attachments);
                    var attachmentsData = restApi.Query(attachmentQuery);
                    foreach (var attachment in attachmentsData.Results)
                    {
                        var content = restApi.GetByReference(attachment._ref, "Content");
                        for (int i = 0; i < 3; i++)
                        {
                            try
                            {
                                var content2 = restApi.GetByReference(content.Content._ref, "Content");
                                IssueAttachment issueAttachment = new IssueAttachment();
                                issueAttachment.Content = System.Convert.FromBase64String(content2.Content);
                                issueAttachment.ContentType = attachment.ContentType;
                                issueAttachment.ContentLength = issueAttachment.Content.Length;
                                issueAttachment.Name = attachment.Name;
                                issue.Attachments.Add(issueAttachment);
                                break;
                            }
                            catch
                            {
                                System.Threading.Thread.Sleep(1000);
                            }
                        }
                    }
                }

                issue.Tag1 = task.FormattedID ?? string.Empty;

                var dto = IssueManager.Create(issue);
                _issues.Add(task._refObjectUUID, dto.Entity.Id);

                foreach (var c in _customFields)
                {
                    string cValue = GetProperty<string>(task, c.Key) ?? string.Empty;
                    CustomFieldData cf = new CustomFieldData();
                    cf.CustomFieldId = c.Value;
                    cf.Data = cValue;
                    cf.ProjectId = dto.Entity.ProjectId;
                    cf.IssueId = dto.Entity.Id;
                    cf.UserId = -2;
                    CustomFieldManager.Update(cf);
                }

                if (_cfRallyId != 0)
                {
                    CustomFieldData cf = new CustomFieldData();
                    cf.ProjectId = dto.Entity.ProjectId;
                    cf.IssueId = dto.Entity.Id;
                    cf.UserId = -2;
                    cf.CustomFieldId = _cfRallyId;
                    cf.Data = task.FormattedID;
                    CustomFieldManager.Update(cf);
                }

                if (_cfBlocked != 0)
                {
                    CustomFieldData cf = new CustomFieldData();
                    cf.ProjectId = dto.Entity.ProjectId;
                    cf.IssueId = dto.Entity.Id;
                    cf.UserId = -2;
                    cf.CustomFieldId = _cfBlocked;
                    cf.Data = task.Blocked ? "Y" : string.Empty;
                    CustomFieldManager.Update(cf);
                }

                if (_cfBlockedReason != 0)
                {
                    CustomFieldData cf = new CustomFieldData();
                    cf.ProjectId = dto.Entity.ProjectId;
                    cf.IssueId = dto.Entity.Id;
                    cf.UserId = -2;
                    cf.CustomFieldId = _cfBlockedReason;
                    cf.Data = GetProperty<string>(task, "BlockedReason") ?? string.Empty;
                    CustomFieldManager.Update(cf);
                }

                string notes = GetProperty<string>(task, "Notes");
                if (notes.HasValue())
                {
                    IssueComment comment = new IssueComment();
                    comment.IssueId = dto.Entity.Id;
                    comment.ProjectId = dto.Entity.ProjectId;
                    comment.Comment = notes;
                    comment.UserId = -2;
                    IssueManager.IssueCommentCreate(comment);
                }

                decimal? timeSpent = GetProperty<decimal?>(task, "TimeSpent");
                if (timeSpent.HasValue && timeSpent > 0)
                {
                    IssueTimeTracking time = new IssueTimeTracking();
                    time.Hours = (int)timeSpent;
                    time.Minutes = (int)(timeSpent - (int)timeSpent) * 60;
                    time.IssueId = dto.Id;
                    time.ProjectId = issue.ProjectId;
                    time.UserId = -2;
                    time.TimeTypeId = _timeType; //32; // Internal
                    time.EntryDate = System.DateTime.Today;
                    timeManager.Create(time);

                }

                _tasksImported++;
                /*var Discussion = task.Discussion;
                if (Discussion.Count > 0)
                {
                    System.Diagnostics.Debugger.Break();
                }*/

                /*
                var _rallyAPIMajor = task._rallyAPIMajor;
                var _rallyAPIMinor = task._rallyAPIMinor;
                var _ref = task._ref;
                var _refObjectUUID = task._refObjectUUID;
                var _objectVersion = task._objectVersion;
                var _refObjectName = task._refObjectName;
                var CreationDate = task.CreationDate;
                var _CreatedAt = task._CreatedAt;
                var ObjectID = task.ObjectID;
                var VersionId = task.VersionId;
                var Subscription = task.Subscription;
                var Workspace = task.Workspace;
                var Changesets = task.Changesets;
                var Description = task.Description;
                var Discussion = task.Discussion;
                var Expedite = task.Expedite;
                var FormattedID = task.FormattedID;
                var LastUpdateDate = task.LastUpdateDate;
                var Name = task.Name;
                var Notes = task.Notes;
                var Owner = task.Owner;
                var Ready = task.Ready;
                var RevisionHistory = task.RevisionHistory;
                var Tags = task.Tags;
                var Attachments = task.Attachments;
                var Blocked = task.Blocked;
                var Estimate = task.Estimate;
                var Iteration = task.Iteration;
                var Project = task.Project;
                var Recycled = task.Recycled;
                var Release = task.Release;
                var State = task.State;
                var TaskIndex = task.TaskIndex;
                var TimeSpent = task.TimeSpent;
                var ToDo = task.ToDo;
                var WorkProduct = task.WorkProduct;
                var c_ExternalId = task.c_ExternalId;
                var _type = task._type;

                var i = task.FormattedID;*/
            }
        }

        #endregion
    }
}
