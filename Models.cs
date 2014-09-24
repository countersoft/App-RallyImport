using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web.Mvc;
using Countersoft.Gemini.Commons.Entity;

namespace RallyImport
{
    public class RallyProject
    {
        public string Name { get; set; }
        public string Id { get; set; }
    }

    public class MappingModel
    {
        public MultiSelectList Projects { get; set; }
        public SelectList Templates { get; set; }

        public string Url { get; set; }
        public string Password { get; set; }
        public string Username { get; set; }

        public MappingModel()
        {
            
        }
    }

    public class StatusModel
    {
        public string Status;
        public List<string> Messages = new List<string>();
    }
}
