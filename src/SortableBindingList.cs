using System.ComponentModel;

namespace ClashTraffic;
public sealed record ByteQuantity(double Value):IComparable,IComparable<ByteQuantity>
{
    public int CompareTo(ByteQuantity? other)=>other==null?1:Value.CompareTo(other.Value);
    public int CompareTo(object? other)=>other is ByteQuantity b?CompareTo(b):other==null?1:throw new ArgumentException("不能比较不同类型");
    public override string ToString()=>Routing.Format(Value);
}
public sealed class SortableBindingList<T>:BindingList<T>
{
    private List<T> allItems;private readonly int visibleLimit;
    public SortableBindingList(List<T> items,int visibleLimit=int.MaxValue):base(items.Take(visibleLimit).ToList()){allItems=items;this.visibleLimit=visibleLimit;}
    private bool sorted;private PropertyDescriptor? property;private ListSortDirection direction;
    protected override bool SupportsSortingCore=>true;
    protected override bool IsSortedCore=>sorted;
    protected override PropertyDescriptor? SortPropertyCore=>property;
    protected override ListSortDirection SortDirectionCore=>direction;
    protected override void ApplySortCore(PropertyDescriptor prop,ListSortDirection sortDirection)
    {
        int Compare(object? a,object? b)
        {
            if(a is null)return b is null?0:-1;if(b is null)return 1;
            return a is string sa&&b is string sb?StringComparer.CurrentCultureIgnoreCase.Compare(sa,sb):a is IComparable c?c.CompareTo(b):StringComparer.CurrentCultureIgnoreCase.Compare(a.ToString(),b.ToString());
        }
        var ordered=allItems.Select((item,index)=>(Item:item,Index:index)).ToList();
        ordered.Sort((a,b)=>{int c=Compare(prop.GetValue(a.Item),prop.GetValue(b.Item));if(sortDirection==ListSortDirection.Descending)c=-c;return c!=0?c:a.Index.CompareTo(b.Index);});
        RaiseListChangedEvents=false;
        allItems=ordered.Select(x=>x.Item).ToList();Items.Clear();foreach(var item in allItems.Take(visibleLimit))Items.Add(item);
        RaiseListChangedEvents=true;property=prop;direction=sortDirection;sorted=true;ResetBindings();
    }
    protected override void RemoveSortCore(){sorted=false;property=null;ResetBindings();}
}
