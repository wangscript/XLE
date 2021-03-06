﻿// Replacement for SearchResultsListView and DomNodeSearchResultsListView with some customizations 
// for the level editor.
// There is no easy way to share the code with the more generic version in the ATF framework. There
// are some hacks we could employ -- but they are non-ideal. The best solution is to just copy the code.
// Most changes are related to the way we select items and update the ISelectionContext

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.ComponentModel;

using Sce.Atf.Applications;
using Sce.Atf.Dom;
using Sce.Atf;
using Sce.Atf.Adaptation;

namespace LevelEditor.Search
{
    public abstract class SearchResultsListView : ListView, IResultsUI
    {
        /// <summary>
        /// Constructor with IContextRegistry</summary>
        /// <param name="contextRegistry">Context registry</param>
        public SearchResultsListView(IContextRegistry contextRegistry)
        {
            // Configure ListView
            View = View.Details;
            LabelEdit = false;
            AllowColumnReorder = false;
            CheckBoxes = false;
            FullRowSelect = true;
            MultiSelect = true;
            GridLines = true;
            Sorting = SortOrder.Ascending;

            _syncContext = System.Threading.SynchronizationContext.Current;
            _selectionUpdateTimer = new System.Threading.Timer(
                (t) =>
                {
                    var listView = ((WeakReference)t).Target as SearchResultsListView;
                    if (listView != null)
                        listView._syncContext.Post(new System.Threading.SendOrPostCallback(UpdateSelectionTick), listView);
                }, 
                new WeakReference(this), 100, System.Threading.Timeout.Infinite);
            SelectedIndexChanged += SearchResultsListView_SelectedIndexChanged;
        }

        #region SelectedItemsHandling
        private void SearchResultsListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            // We get mutliple "SelectedIndexChanged" events for a single user operation. However,
            // we don't really know which will be the last. We can try to optimize this by delaying
            // for a short while, and only processing the last event.
            _selectionUpdateTimer.Change(100, System.Threading.Timeout.Infinite);
        }

        private static void UpdateSelectionTick(object client)
        {
            var listView = (SearchResultsListView)client;
            var selectionContext = listView.m_queryResultContext.As<ISelectionContext>();
            if (selectionContext != null)
                selectionContext.Selection = listView.GetSelection();
        }

        private IEnumerable<object> GetSelection()
        {
            foreach (var s in SelectedItems) {
                var node = ((ListViewItem)s).Tag.As<DomNode>();
                if (node != null)
                    yield return LevelEditorCore.Util.AdaptDomPath(node);
            }
        }

        private readonly System.Threading.Timer _selectionUpdateTimer;
        private readonly System.Threading.SynchronizationContext _syncContext;
        #endregion

        /// <summary>
        /// Constructor with IQueryableResultContext and IContextRegistry</summary>
        /// <param name="queryResultContext">Query result context</param>
        /// <param name="contextRegistry">Context registry</param>
        public SearchResultsListView(IQueryableResultContext queryResultContext, IContextRegistry contextRegistry)
            : this(contextRegistry)
        {
            Bind(queryResultContext);
        }

        #region IResultsControl members
        /// <summary>
        /// Binds this search Control to a data set (that is wrapped in a class implementing IQueryableContext)</summary>
        /// <param name="queryResultContext">Search results to bind to</param>
        public void Bind(IQueryableResultContext queryResultContext)
        {
            ClearResults();

            if (m_queryResultContext != null)
                m_queryResultContext.ResultsChanged -= queryResultContext_ResultsChanged;

            m_queryResultContext = queryResultContext;

            if (m_queryResultContext != null)
                m_queryResultContext.ResultsChanged += queryResultContext_ResultsChanged;
        }

        /// <summary>
        /// Gets actual client-defined GUI Control</summary>
        public Control Control { get { return this; } }

        /// <summary>
        /// Event raised by client when UI has graphically changed</summary>
        public abstract event EventHandler UIChanged;
        #endregion

        /// <summary>
        /// Callback called when the query result context has updated its results. Rebuilds the ListView display from results.</summary>
        /// <param name="sender">Sender of the event</param>
        /// <param name="e">Event arguments</param>
        private void queryResultContext_ResultsChanged(object sender, System.EventArgs e)
        {
            UpdateResults();
        }

        /// <summary>
        /// Clears the previous result list</summary>
        protected abstract void ClearResults();

        /// <summary>
        /// Updates the result list</summary>
        protected abstract void UpdateResults();

        /// <summary>
        /// Gets the query result context</summary>
        protected IQueryableResultContext QueryResultContext
        {
            get
            {
                if (m_queryResultContext == null)
                    throw new InvalidOperationException("Search Results UI isn't bound to a data set.");
                return m_queryResultContext;
            }
        }

        private IQueryableResultContext m_queryResultContext;
    }

    public class DomNodeSearchResultsListView : SearchResultsListView
    {
        /// <summary>
        /// Constructor</summary>
        public DomNodeSearchResultsListView()
            : base(null)
        {
            // Create columns for the items and subitems.
            // Width of -2 indicates auto-size.
            Columns.Add("Node Name", -2, HorizontalAlignment.Center);
            Columns.Add("Type", -2, HorizontalAlignment.Center);
            Columns.Add("Property", -2, HorizontalAlignment.Right);
            Columns.Add("Value", -2, HorizontalAlignment.Left);

            if (UIChanged == null) return;
        }

        /// <summary>
        /// Constructor</summary>
        /// <param name="contextRegistry">Interface to dataset that has been been searched</param>
        public DomNodeSearchResultsListView(IContextRegistry contextRegistry)
            : base(contextRegistry)
        {
            // Create columns for the items and subitems.
            // Width of -2 indicates auto-size.
            Columns.Add("Node Name", -2, HorizontalAlignment.Center);
            Columns.Add("Type", -2, HorizontalAlignment.Center);
            Columns.Add("Property", -2, HorizontalAlignment.Right);
            Columns.Add("Value", -2, HorizontalAlignment.Left);

            if (UIChanged == null) return;
        }

        /// <summary>
        /// Event raised by client when UI has graphically changed</summary>
        public override event EventHandler UIChanged;

        /// <summary>
        /// Clears the previous result list</summary>
        protected override void ClearResults()
        {
            // Clear out the previous result list
            foreach (ListViewItem item in Items)
            {
                DomNode domNode = (DomNode)item.Tag;
                if (domNode != null)
                    domNode.AttributeChanged -= DomNode_Changed;
            }
            Columns.Clear();
            Items.Clear();
        }

        /// <summary>
        /// Updates the result list</summary>
        protected override void UpdateResults()
        {
            BeginUpdate();
            try
            {
                ClearResults();

                // ListView header and items will be built from the contents of these lists
                List<HeaderData> headerList = new List<HeaderData>();
                List<ListViewItem> itemList = new List<ListViewItem>();

                // Create a new list item for each "matching" DomNode (ie, one that contains one or more matching properties)
                foreach (object resultObject in QueryResultContext.Results)
                {
                    // DomNodeQueryMatch is a DomNode, paired with a list of its properties that matched the search
                    DomNodeQueryMatch queryMatch = resultObject as DomNodeQueryMatch;

                    if (queryMatch == null)
                        throw new InvalidOperationException("The class implementing IQueryableContext, which produced the results passed in, did not create results of type DomNodeQueryMatch.  Consider use DomNodeQueryable to create your search results.");

                    // Add the "Node Name" and "Type" headers if none have been created yet
                    int headerIndex = 0;
                    if (headerList.Count <= headerIndex)
                    {
                        headerList.Add(new HeaderData("Node Name", HorizontalAlignment.Left));
                        headerList.Add(new HeaderData("Type", HorizontalAlignment.Center));
                    }

                    // Display DomNode name and type
                    string nodeName = GetDomNodeName(queryMatch.DomNode);
                    string nodeType = queryMatch.DomNode.Type.ToString();

                    // This will allow the results list to be updated when one of its items has changed
                    // (for instance, from an 'undo' or 'redo')
                    queryMatch.DomNode.AttributeChanged += DomNode_Changed;

                    ListViewItem newItem = new ListViewItem(new string[2] { nodeName, nodeType });
                    newItem.Tag = queryMatch.DomNode;

                    headerList[headerIndex++].RegisterColumnString(nodeName);
                    headerList[headerIndex++].RegisterColumnString(nodeType);

                    // After the DomNode's name and type, include the name and value of each property of this DomNode that matched
                    foreach (object predicateResult in queryMatch.PredicateMatchResults.Values)
                    {
                        List<IQueryMatch> matchingItems = predicateResult as List<IQueryMatch>;
                        if (matchingItems != null)
                        {
                            foreach (DomNodePropertyMatch matchingItem in matchingItems)
                            {
                                // Add additional "Property" and "Value" headers (if need be)
                                if (headerList.Count <= headerIndex)
                                {
                                    headerList.Add(new HeaderData("Property", HorizontalAlignment.Right));
                                    headerList.Add(new HeaderData("Value", HorizontalAlignment.Left));
                                }

                                // Each property name and value pair are added as subitems of the list item
                                string newPropertyName = matchingItem.Name;
                                string newPropertyValue = matchingItem.GetValue().ToString();

                                // Add each subitem, tagged with the PropertyDescriptor to which it is associated
                                ListViewItem.ListViewSubItem subItemName = new ListViewItem.ListViewSubItem(newItem, newPropertyName);
                                ListViewItem.ListViewSubItem subItemValue = new ListViewItem.ListViewSubItem(newItem, newPropertyValue);
                                subItemName.Tag = matchingItem.PropertyDescriptor;
                                subItemValue.Tag = matchingItem.PropertyDescriptor;
                                newItem.SubItems.Add(subItemName);
                                newItem.SubItems.Add(subItemValue);

                                headerList[headerIndex++].RegisterColumnString(newPropertyName);
                                headerList[headerIndex++].RegisterColumnString(newPropertyValue);
                            }
                        }
                    }

                    // Add the matching DomNode, with all matching properties, to the results list
                    itemList.Add(newItem);
                }

                // All search results have been reviewed, and all header data from it has been created.
                // Build the ListView header from the header data
                var g = CreateGraphics();
                foreach (HeaderData headerData in headerList)
                {
                    // Width of header determined by longest string found in that column
                    int width = g.MeasureString(headerData.LongestString, Font).ToSize().Width + 15;
                    Columns.Add(headerData.Name, width, headerData.HorizontalAlignment);
                }

                // Build the ListView items
                Items.AddRange(itemList.ToArray());
            }
            finally
            {
                EndUpdate();
            }
        }

        private void DomNode_Changed(object sender, EventArgs e)
        {
            UpdateResults();
        }

        /// <summary>
        /// Gets the collection of PropertyDescriptors that make up a DomNode instance</summary>
        /// <param name="node">The DomNode whose properties are retrieved</param>
        /// <returns>The collection of PropertyDescriptors owned by the specified DomNode</returns>
        static internal PropertyDescriptorCollection GetDomNodeProperties(DomNode node)
        {
            if (node == null)
                return null;

            ICustomTypeDescriptor iCustomTypeDescriptor = node.GetAdapter(typeof(CustomTypeDescriptorNodeAdapter)) as ICustomTypeDescriptor;
            if (iCustomTypeDescriptor == null)
                return null;

            PropertyDescriptorCollection properties = iCustomTypeDescriptor.GetProperties();
            return properties;
        }

        /// <summary>
        /// Gets the string value of the "Name" PropertyDescriptor of a DomNode</summary>
        /// <param name="domNode">The DomNode whose name property is retrieved</param>
        /// <returns>String value of the specified DomNode's "Name"</returns>
        static internal string GetDomNodeName(DomNode domNode)
        {
            string name = "<UNKNOWN>";
            PropertyDescriptorCollection properties = GetDomNodeProperties(domNode);
            if (properties != null)
            {
                foreach (System.ComponentModel.PropertyDescriptor property in properties)
                {
                    if (property.Name.Equals("Name", StringComparison.InvariantCultureIgnoreCase))
                    {
                        name = property.GetValue(domNode) as string;
                        break;
                    }
                }
            }
            return name;
        }

        /// <summary>
        /// Manages the string displayed for each header, as well as the longest string in that column</summary>
        private class HeaderData
        {
            /// <summary>
            /// Constructor - private to prevent default construction</summary>
            private HeaderData() { }

            /// <summary>
            /// Constructor</summary>
            /// <param name="name">String for header</param>
            /// <param name="horizontalAlignment">Alignment of name string in header</param>
            public HeaderData(string name, HorizontalAlignment horizontalAlignment)
            {
                m_name = name;
                m_longestString = name;
                m_horizontalAlignment = horizontalAlignment;
            }

            /// <summary>
            /// Checks if registered string is longest string in that column. Save it if so.</summary>
            /// <param name="columnString">String to check</param>
            public void RegisterColumnString(string columnString)
            {
                if (columnString.Length > m_longestString.Length)
                    m_longestString = columnString;
            }

            /// <summary>
            /// Gets string for header</summary>
            public string Name { get { return m_name; } }
            /// <summary>
            /// Gets longest string in that column</summary>
            public string LongestString { get { return m_longestString; } }
            /// <summary>
            /// Gets alignment of name string in header</summary>
            public HorizontalAlignment HorizontalAlignment { get { return m_horizontalAlignment; } }

            private readonly string m_name;
            private string m_longestString;
            private readonly HorizontalAlignment m_horizontalAlignment;
        }
    }
}
