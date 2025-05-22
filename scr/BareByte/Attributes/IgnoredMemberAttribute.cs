using System;
using System.Collections.Generic;
using System.Text;

namespace BareByte.Attributes
{
    [AttributeUsage(AttributeTargets.Property)]
    public  class IgnoredMemberAttribute : Attribute { }
}
