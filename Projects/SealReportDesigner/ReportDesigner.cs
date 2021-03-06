﻿//
// Copyright (c) Seal Report, Eric Pfirsch (sealreport@gmail.com), http://www.sealreport.org.
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the License. http://www.apache.org/licenses/LICENSE-2.0..
//
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Data.OleDb;
using Seal.Model;
using System.IO;
using Seal.Controls;
using Seal.Helpers;
using Seal.Converter;
using Seal.Forms;
using System.Diagnostics;
using RazorEngine.Templating;
using System.Collections;
using System.ComponentModel.Design;
using System.Drawing.Design;
using System.Reflection;
using Microsoft.Win32.TaskScheduler;

namespace Seal
{
    public partial class ReportDesigner : Form, IEntityHandler
    {

        #region Members

        TreeViewEditorHelper treeViewHelper;
        ToolStripEditorHelper toolStripHelper;
        ToolsHelper toolsHelper;

        Report _report = null;
        public Report Report
        {
            get { return _report; }
        }

        bool _canRender = false;
        public void CannotRenderAnymore()
        {
            _canRender = false;
        }

        bool _isModified = false;
        public bool IsModified
        {
            get { return _isModified; }
            set
            {
                _isModified = value;
                //Modification of a meta item, a model...means that we can not render anymore
                if (selectedEntity is ReportSource || selectedEntity is MetaColumn || selectedEntity is MetaConnection || selectedEntity is MetaJoin || selectedEntity is MetaTable || selectedEntity is MetaEnum) _canRender = false;
                enableControls();
            }
        }

        ModelPanel modelPanel = new ModelPanel();
        ViewPanel viewPanel = new ViewPanel();
        Repository _repository;
        ReportViewerForm _reportViewer = null;

        public ReportDesigner()
        {
            if (Properties.Settings.Default.CallUpgrade)
            {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.CallUpgrade = false;
                Properties.Settings.Default.Save();
            }

            InitializeComponent();
            mainPropertyGrid.PropertySort = PropertySort.Categorized;

            treeViewHelper = new TreeViewEditorHelper() { Report = _report, sortColumnAlphaOrderToolStripMenuItem = sortColumnAlphaOrderToolStripMenuItem, sortColumnSQLOrderToolStripMenuItem = sortColumnSQLOrderToolStripMenuItem, addFromToolStripMenuItem = addFromToolStripMenuItem, addToolStripMenuItem = addToolStripMenuItem, removeToolStripMenuItem = removeToolStripMenuItem, copyToolStripMenuItem = copyToolStripMenuItem, removeRootToolStripMenuItem = removeRootToolStripMenuItem, treeContextMenuStrip = treeContextMenuStrip, mainTreeView = mainTreeView, ForReport = true };
            mainTreeView.AfterSelect += treeViewHelper.AfterSelect;

            toolStripHelper = new ToolStripEditorHelper() { MainToolStrip = mainToolStrip, MainPropertyGrid = mainPropertyGrid, EntityHandler = this, MainTreeView = mainTreeView };
            toolsHelper = new ToolsHelper() { EntityHandler = this };
            toolsHelper.InitHelpers(toolsToolStripMenuItem, true);

            HelperEditor.HandlerInterface = this;


            mainSplitContainer.Panel2.Controls.Add(modelPanel);
            modelPanel.Dock = DockStyle.Fill;
            mainSplitContainer.Panel2.Controls.Add(viewPanel);
            viewPanel.Dock = DockStyle.Fill;

            ShowIcon = true;
            Icon = Properties.Resources.reportDesigner;
        }

        private void ReportDesigner_Load(object sender, EventArgs e)
        {
                KeyPreview = true;

                //Set event handler for sub-property grids...
                EntityCollectionEditor.MyPropertyValueChanged += mainPropertyGrid_PropertyValueChanged;

                //handle program args
                string[] args = Environment.GetCommandLineArgs();
                bool open = (args.Length >= 2 && args[1].ToLower() == "/o");
                string reportToOpen = null;
                if (args.Length >= 3 && File.Exists(args[2])) reportToOpen = args[2];

                //MRU = most recent used reports
                if (Properties.Settings.Default.MRU == null) Properties.Settings.Default.MRU = new System.Collections.Specialized.StringCollection();
                if (!open && Properties.Settings.Default.MRU.Count > 0 && File.Exists(Properties.Settings.Default.MRU[0]))
                {
                    open = true;
                    reportToOpen = Properties.Settings.Default.MRU[0];
                }

                showScriptErrorsToolStripMenuItem.Checked = Properties.Settings.Default.ShowScriptErrors;
                if (!Helper.IsMachineAdministrator()) Properties.Settings.Default.SchedulesWithCurrentUser = true;
                schedulesWithCurrentUserToolStripMenuItem.Checked = Properties.Settings.Default.SchedulesWithCurrentUser;

                if (open)
                {
                    if (!string.IsNullOrEmpty(reportToOpen)) openReport(reportToOpen);
                    else newToolStripMenuItem_Click(null, null);
                }
                else
                {
                    _repository = Repository.Create();
                    IsModified = false;
                    init();
                }
            if (_repository == null)
            {
                _repository = new Repository();
                MessageBox.Show("No repository has been defined or found for this installation. Reports will not be rendered. Please modify the .config file to set a RepositoryPath containing at least a Views subfolder", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        //EntityHandlerInterface
        public void SetModified()
        {
            IsModified = true;
        }

        public void InitEntity(object entity)
        {
            init(entity);
        }

        public void EditSchedule(ReportSchedule schedule)
        {
            try
            {
                TaskEditDialog frm = new TaskEditDialog(schedule.Task, true, true);
                frm.AvailableTabs = AvailableTaskTabs.General | AvailableTaskTabs.Actions | AvailableTaskTabs.Conditions | AvailableTaskTabs.Triggers | AvailableTaskTabs.Settings | AvailableTaskTabs.History;
                frm.ShowDialog(this);
            }
            catch (Exception ex)
            {
                throw new Exception(string.Format("Unable to edit the schedule...\r\nCheck that the 'Report Designer' has been executed with the option 'Run as administrator'\r\nOR\r\nSet the Report Designer option 'Schedule reports with current user.\r\n\r\n{0}", ex.Message));
            }
        }

        #endregion

        #region Helpers

        void initTreeNodeViews(TreeNode node, ReportView view)
        {
            if (view != null)
            {
                foreach (var childView in view.Views)
                {
                    TreeNode childNode = new TreeNode(childView.Name) { ImageIndex = 8, SelectedImageIndex = 8 };
                    childNode.Tag = childView;
                    node.Nodes.Add(childNode);
                    initTreeNodeViews(childNode, childView);
                }
            }
        }

        void init(object entityToSelect = null)
        {
            if (entityToSelect == null && mainTreeView.SelectedNode != null) entityToSelect = mainTreeView.SelectedNode.Tag;

            treeViewHelper.Report = _report;
            if (_report == null)
            {
                mainSplitContainer.Visible = false;
            }
            else
            {
                mainSplitContainer.Visible = true;

                mainTreeView.Nodes.Clear();
                TreeNode sourceTN = new TreeNode("Sources") { Tag = new SourceFolder(), ImageIndex = 2, SelectedImageIndex = 2 };
                mainTreeView.Nodes.Add(sourceTN);
                foreach (var source in _report.Sources)
                {
                    treeViewHelper.addSource(sourceTN.Nodes, source, 13);
                }
                sourceTN.Expand();

                TreeNode modelTN = new TreeNode("Models") { Tag = new ModelFolder(), ImageIndex = 2, SelectedImageIndex = 2 };
                mainTreeView.Nodes.Add(modelTN);
                foreach (var model in _report.Models)
                {
                    TreeNode tn = new TreeNode(model.Name) { Tag = model, ImageIndex = 10, SelectedImageIndex = 10 };
                    tn.Tag = model;
                    modelTN.Nodes.Add(tn);
                }
                modelTN.Expand();

                TreeNode viewTN = new TreeNode("Views") { Tag = new ViewFolder() { Report = Report }, ImageIndex = 8, SelectedImageIndex = 8 };
                mainTreeView.Nodes.Add(viewTN);
                foreach (ReportView view in _report.Views)
                {
                    TreeNode reportViewTN = new TreeNode(view.Name) { ImageIndex = 8, SelectedImageIndex = 8 };
                    reportViewTN.Tag = view;
                    viewTN.Nodes.Add(reportViewTN);
                    initTreeNodeViews(reportViewTN, view);
                }
                viewTN.ExpandAll();

                TreeNode tasksTN = new TreeNode("Tasks") { Tag = new TasksFolder() { Report = Report }, ImageIndex = 2, SelectedImageIndex = 2 };
                mainTreeView.Nodes.Add(tasksTN);
                foreach (var task in _report.Tasks)
                {
                    TreeNode taskTN = new TreeNode(task.Name) { Tag = task, ImageIndex = 12, SelectedImageIndex = 12 };
                    tasksTN.Nodes.Add(taskTN);
                }
                tasksTN.Expand();

                TreeNode outputsTN = new TreeNode("Outputs") { Tag = new OutputFolder(), ImageIndex = 2, SelectedImageIndex = 2 };
                mainTreeView.Nodes.Add(outputsTN);
                foreach (var output in _report.Outputs)
                {
                    TreeNode outputTN = new TreeNode(output.Name) { Tag = output, ImageIndex = 9, SelectedImageIndex = 9 };
                    outputsTN.Nodes.Add(outputTN);
                }
                outputsTN.Expand();

                TreeNode schedulesTN = new TreeNode("Schedules") { Tag = new ScheduleFolder(), ImageIndex = 2, SelectedImageIndex = 2 };
                mainTreeView.Nodes.Add(schedulesTN);
                foreach (var schedule in _report.Schedules)
                {
                    TreeNode scheduleTN = new TreeNode(schedule.Name) { Tag = schedule, ImageIndex = 11, SelectedImageIndex = 11 };
                    schedulesTN.Nodes.Add(scheduleTN);
                }
                schedulesTN.Expand();

                if (mainTreeView.SelectedNode == null)
                {
                    mainTreeView.SelectedNode = sourceTN;
                }
            }
            if (entityToSelect != null) selectNode(entityToSelect);

            enableControls();
            buildMRUMenus();
        }

        void enableControls()
        {
            Text = Repository.SealRootProductName + " Report Designer";
            if (_report != null)
            {
                if (_report.SchedulesModified) _isModified = true;
                Text = Path.GetFileNameWithoutExtension(_report.FilePath) + (IsModified ? "*" : "") + " - " + Text;
            }

            saveToolStripMenuItem.Enabled = (_report != null);
            saveToolStripButton.Enabled = saveToolStripMenuItem.Enabled;
            saveAsToolStripMenuItem.Enabled = (_report != null);
            reloadToolStripMenuItem.Enabled = (_report != null && !string.IsNullOrEmpty(Path.GetDirectoryName(_report.FilePath)));
            MRUToolStripMenuItem.Enabled = (MRUToolStripMenuItem.DropDownItems.Count > 0);
            closeToolStripMenuItem.Enabled = (_report != null);
            executeToolStripMenuItem.Enabled = (_report != null && (_reportViewer == null || (_reportViewer != null && _reportViewer.CanExecute)));
            executeToolStripButton.Enabled = executeToolStripMenuItem.Enabled;
            renderToolStripMenuItem.Enabled = (_canRender && _report != null && _reportViewer != null && _reportViewer.Visible && _reportViewer.CanRender);
            renderToolStripButton.Enabled = renderToolStripMenuItem.Enabled;

            bool showViewOutput = (selectedEntity is ReportView || selectedEntity is ReportOutput);
            executeViewOutputToolStripMenuItem.Visible = showViewOutput;
            renderViewOutputToolStripMenuItem.Visible = showViewOutput;
            executeViewOutputToolStripButton.Visible = showViewOutput;
            renderViewOutputToolStripButton.Visible = showViewOutput;

            executeViewOutputToolStripMenuItem.Text = (selectedEntity is ReportView ? "Execute View" : "Execute Output");
            renderViewOutputToolStripMenuItem.Text = (selectedEntity is ReportView ? "Render View" : "Render Output");

            executeViewOutputToolStripButton.Text = executeViewOutputToolStripMenuItem.Text;
            executeViewOutputToolStripButton.ToolTipText = (selectedEntity is ReportView ? "F10 Execute the report using the selected view" : "F10 Execute the selected report output"); ;
            renderViewOutputToolStripButton.Text = renderViewOutputToolStripMenuItem.Text;
            renderViewOutputToolStripButton.ToolTipText = (selectedEntity is ReportView ? "F11 Execute the report using the models of the previous execution and the selected view" : "F11 Execute the selected report output using the models of the previous execution");

            executeViewOutputToolStripMenuItem.Enabled = executeToolStripMenuItem.Enabled;
            renderViewOutputToolStripMenuItem.Enabled = renderToolStripMenuItem.Enabled;
            executeViewOutputToolStripButton.Enabled = executeToolStripMenuItem.Enabled;
            renderViewOutputToolStripButton.Enabled = renderToolStripMenuItem.Enabled;

            toolsHelper.EnableControls();
        }

        bool checkModified()
        {
            bool result = true;
            if (_report != null && IsModified)
            {
                DialogResult dlgResult = MessageBox.Show("The current report has been modified, do you want to save it ?", "Warning", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (dlgResult == DialogResult.Cancel) result = false;
                else if (dlgResult == DialogResult.Yes) saveToolStripMenuItem_Click(null, null);
            }
            return result;
        }

        object selectedEntity
        {
            get
            {
                object result = null;
                if (mainTreeView.SelectedNode != null) result = mainTreeView.SelectedNode.Tag;
                return result;
            }
        }

        bool isChildNodeSelected
        {
            get
            {
                return (mainTreeView.SelectedNode != null && mainTreeView.SelectedNode.Parent != null);
            }
        }

        void selectNode(object entity)
        {
            TreeViewHelper.SelectNode(mainTreeView, mainTreeView.Nodes, entity);
        }

        #endregion

        #region Main Form Handlers

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void ReportDesigner_FormClosing(object sender, FormClosingEventArgs e)
        {
#if DEBUG
            if (_repository != null) _repository.FlushTranslationUsage();
#endif
            Properties.Settings.Default.Save();
            if (!checkModified()) e.Cancel = true;
        }

        private void buildMRUMenus()
        {
            //Check and clean up MRUs
            int i = Properties.Settings.Default.MRU.Count;
            while (--i >= 0)
            {
                if (i >= 10 || !File.Exists(Properties.Settings.Default.MRU[i])) Properties.Settings.Default.MRU.RemoveAt(i);
            }

            MRUToolStripMenuItem.DropDownItems.Clear();
            foreach (var mru in Properties.Settings.Default.MRU)
            {
                ToolStripMenuItem item = new ToolStripMenuItem(mru);
                item.Click += new EventHandler(delegate(object sender, EventArgs e)
                {
                    if (!checkModified()) return;
                    openReport(((ToolStripMenuItem)sender).Text);
                });
                MRUToolStripMenuItem.DropDownItems.Add(item);
            }
        }

        private void addMRU(string fileName)
        {
            Properties.Settings.Default.MRU.Remove(fileName);
            Properties.Settings.Default.MRU.Insert(0, fileName);
            Properties.Settings.Default.Save();
            buildMRUMenus();
        }

        private void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!checkModified()) return;

            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = string.Format(Repository.SealRootProductName + " Reports files (*.{0})|*.{0}|All files (*.*)|*.*", Repository.SealReportFileExtension);
            dlg.Title = "Open a report";
            dlg.CheckFileExists = true;
            dlg.CheckPathExists = true;
            if (_report != null) dlg.InitialDirectory = Path.GetDirectoryName(_report.FilePath);
            if (string.IsNullOrEmpty(dlg.InitialDirectory)) dlg.InitialDirectory = _repository.ReportsFolder;
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                if (_reportViewer != null && _reportViewer.Visible) _reportViewer.Close();
                openReport(dlg.FileName);
            }
        }

        private void reloadToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_report != null)
            {
                if (IsModified)
                {
                    DialogResult dlgResult = MessageBox.Show("The current report has been modified, are you sure you to reload it ?", "Warning", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                    if (dlgResult == DialogResult.Cancel) return;
                }
                if (_reportViewer != null && _reportViewer.Visible) _reportViewer.Close();

                string path = _report.FilePath;
                _report = null;
                IsModified = false;
                closeToolStripMenuItem_Click(sender, e);
                if (File.Exists(path)) openReport(path);
            }
        }

        private void selectAfterLoad()
        {
            if (_report.Models.Count > 0 && _report.Models[0].Elements.Count > 0) selectNode(_report.Models[0]);
            else if (_report.Tasks.Count > 0) selectNode(_report.Tasks.OrderBy(i => i.SortOrder).First());
            else if (_report.Sources.Count > 0) selectNode(_report.Sources[0]);
        }

        private void openReport(string path)
        {
            //refresh repository
            _repository = Repository.Create();
            _report = Report.LoadFromFile(path, _repository);
            if (_report != null)
            {
                addMRU(path);
                IsModified = false;
                init();
                selectAfterLoad();

                if (!string.IsNullOrEmpty(_report.LoadErrors))
                {
                    MessageBox.Show(string.Format("Error loading the report:\r\n{0}", _report.LoadErrors), "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                if (!string.IsNullOrEmpty(_report.UpgradeWarnings))
                {
                    MessageBox.Show(_report.UpgradeWarnings, "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            if (_reportViewer != null && _reportViewer.Visible) _reportViewer.Close();
            toolsHelper.Report = _report;
            _report.SchedulesWithCurrentUser = Properties.Settings.Default.SchedulesWithCurrentUser;
        }

        private void newToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                if (!checkModified()) return;
                if (_reportViewer != null && _reportViewer.Visible) _reportViewer.Close();

                if (_repository == null || _repository.MustReload()) _repository = Repository.Create();
                _report = Report.Create(_repository);
                IsModified = true;
                mainTreeView.SelectedNode = null;
                init();
                selectAfterLoad();

                toolsHelper.Report = _report;
                _report.SchedulesWithCurrentUser = Properties.Settings.Default.SchedulesWithCurrentUser;
            }
            finally
            {
                Cursor.Current = Cursors.Default;
            }
        }

        private void saveToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_report != null)
            {
                if (sender == saveAsToolStripMenuItem || string.IsNullOrEmpty(Path.GetDirectoryName(_report.FilePath)))
                {
                    SaveFileDialog dlg = new SaveFileDialog();
                    dlg.Filter = string.Format(Repository.SealRootProductName + " Reports files (*.{0})|*.{0}|All files (*.*)|*.*", Repository.SealReportFileExtension);
                    if (_report != null) dlg.InitialDirectory = Path.GetDirectoryName(_report.FilePath);
                    if (string.IsNullOrEmpty(dlg.InitialDirectory)) dlg.InitialDirectory = _repository.ReportsFolder;
                    dlg.FileName = Path.GetFileName(_report.FilePath);
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        if (_reportViewer != null && _reportViewer.Visible) _reportViewer.Close();
                        if (sender == saveAsToolStripMenuItem)
                        {
                            //Save as -> new GUID and no schedule copy...
                            _report.GUID = Guid.NewGuid().ToString();
                            _report.Schedules.Clear();
                        }
                        _report.FilePath = dlg.FileName;
                        init();
                    }
                    else return;
                    _report.LastModification = DateTime.MinValue;
                }
                //commit panels
                if (selectedEntity is ReportModel) modelPanel.Commit();

                _report.SaveToFile();
                addMRU(_report.FilePath);
                IsModified = false;
                enableControls();
            }
        }

        private void closeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!checkModified()) return;
            if (_reportViewer != null && _reportViewer.Visible) _reportViewer.Close();
            _report = null;
            toolStripHelper.SetHelperButtons(null);
            IsModified = false;
            init();
            toolsHelper.Report = _report;
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AboutBoxForm frm = new AboutBoxForm();
            frm.ShowDialog(this);
        }


        private void showScriptErrorsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            showScriptErrorsToolStripMenuItem.Checked = !showScriptErrorsToolStripMenuItem.Checked;
            Properties.Settings.Default.ShowScriptErrors = showScriptErrorsToolStripMenuItem.Checked;
            Properties.Settings.Default.Save();
        }

        private void schedulesWithCurrentUserToolStripMenuItem_Click(object sender, EventArgs e)
        {
            schedulesWithCurrentUserToolStripMenuItem.Checked = !schedulesWithCurrentUserToolStripMenuItem.Checked;
            Properties.Settings.Default.SchedulesWithCurrentUser = schedulesWithCurrentUserToolStripMenuItem.Checked;
            if (_report != null) _report.SchedulesWithCurrentUser = Properties.Settings.Default.SchedulesWithCurrentUser;
            Properties.Settings.Default.Save();
        }

        #endregion

        #region Tree View Handlers

        private void mainTreeView_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right) mainTreeView.SelectedNode = e.Node;
        }


        private void mainTreeView_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                // Select the clicked node
                mainTreeView.SelectedNode = mainTreeView.GetNodeAt(e.X, e.Y);

                if (mainTreeView.SelectedNode != null)
                {
                    treeContextMenuStrip.Show(mainTreeView, e.Location);
                }
            }
        }

        bool _adminWarningDone = false;
        private void mainTreeView_BeforeSelect(object sender, TreeViewCancelEventArgs e)
        {
            if (selectedEntity != null && isChildNodeSelected)
            {
                object newEntity = e.Node.Tag;
                if (!_report.SchedulesWithCurrentUser && !_adminWarningDone && (newEntity is ReportSchedule || newEntity is ScheduleFolder) && !Helper.IsMachineAdministrator())
                {
                    MessageBox.Show("We recommend to execute the 'Report Designer' application with the option 'Run as administrator' to edit the Schedules (part of the Windows Tasks Scheduler)...", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _adminWarningDone = true;
                    e.Cancel = true;
                    return;
                }

                if ((newEntity is ReportSchedule || newEntity is ScheduleFolder) && !Helper.CheckTaskSchedulerOS())
                {
                    e.Cancel = true;
                    return;
                }

                //commit panels
                if (selectedEntity is ReportModel)
                {
                    modelPanel.Commit();
                }
            }
        }


        bool _pdfExpanded = false, _excelExpanded = false;
        private void mainTreeView_AfterSelect(object sender, TreeViewEventArgs e)
        {
            modelPanel.Visible = false;
            viewPanel.Visible = false;
            mainPropertyGrid.Visible = false;

            //refresh source
            foreach (var source in _report.Sources)
            {
                source.Refresh();
            }

            var entry = Helper.GetGridEntry(mainPropertyGrid, "pdf configuration");
            if (entry != null) _pdfExpanded = entry.Expanded;
            entry = Helper.GetGridEntry(mainPropertyGrid, "excel configuration");
            if (entry != null) _excelExpanded = entry.Expanded;

            mainPropertyGrid.SelectedObject = null;
            if (selectedEntity is ReportModel)
            {
                modelPanel.Visible = true;
                modelPanel.Model = (ReportModel)selectedEntity;
                modelPanel.Init(this);
            }
            else if (selectedEntity is RootComponent)
            {
                RootComponent entity = (RootComponent)selectedEntity;
                mainPropertyGrid.Visible = true;
                entity.InitEditor();
                mainPropertyGrid.SelectedObject = selectedEntity;
                //Do not allow edition of repository objects
                if (selectedEntity is MetaConnection && !((MetaConnection)selectedEntity).IsEditable) entity.SetReadOnly();
                if (selectedEntity is MetaTable && !((MetaTable)selectedEntity).IsEditable) entity.SetReadOnly();
                if (selectedEntity is MetaJoin && !((MetaJoin)selectedEntity).IsEditable) entity.SetReadOnly();
                if (selectedEntity is MetaColumn && !((MetaColumn)selectedEntity).MetaTable.IsEditable) entity.SetReadOnly();
                if (selectedEntity is MetaEnum && !((MetaEnum)selectedEntity).IsEditable) entity.SetReadOnly();

                if (selectedEntity is MetaColumn) ((MetaColumn)selectedEntity).HideSubReports();
            }
            else if (selectedEntity is ReportView)
            {
                viewPanel.Visible = true;
                viewPanel.View = (ReportView)selectedEntity;
                viewPanel.Init(this);

            }
            //Set default expanded
            entry = Helper.GetGridEntry(mainPropertyGrid, "pdf configuration");
            if (entry != null) entry.Expanded = _pdfExpanded;
            entry = Helper.GetGridEntry(mainPropertyGrid, "excel configuration");
            if (entry != null) entry.Expanded = _excelExpanded;

            toolStripHelper.SetHelperButtons(selectedEntity);
            enableControls();
        }

        private void mainTreeView_BeforeLabelEdit(object sender, NodeLabelEditEventArgs e)
        {
            e.CancelEdit = true;
            if (mainTreeView.SelectedNode == null) return;
            object entity = mainTreeView.SelectedNode.Tag;
            if (entity is MetaSource || entity is ReportModel || entity is ReportView || entity is ReportTask || entity is ReportOutput || entity is ReportSchedule)
            {
                e.CancelEdit = false;
            }
        }

        private void mainTreeView_AfterLabelEdit(object sender, NodeLabelEditEventArgs e)
        {
            object entity = mainTreeView.SelectedNode.Tag;
            if (!string.IsNullOrEmpty(e.Label))
            {
                e.CancelEdit = true;
                if (entity is MetaSource)
                {
                    e.Node.Text = Helper.GetUniqueName(e.Label, (from i in Report.Sources select i.Name).ToList());
                }
                else if (entity is ReportModel)
                {
                    e.Node.Text = Helper.GetUniqueName(e.Label, (from i in Report.Models select i.Name).ToList());
                }
                else if (entity is ReportView)
                {
                    e.Node.Text = e.Label;
                    if (mainTreeView.SelectedNode.Parent != null && mainTreeView.SelectedNode.Parent.Tag is ReportView)
                    {
                        ReportView parent = (ReportView)mainTreeView.SelectedNode.Parent.Tag;
                        e.Node.Text = Helper.GetUniqueName(e.Label, (from i in parent.Views select i.Name).ToList());
                    }
                    else if (mainTreeView.SelectedNode.Parent != null && mainTreeView.SelectedNode.Parent.Tag is ViewFolder)
                    {
                        e.Node.Text = Helper.GetUniqueName(e.Label, (from i in Report.Views select i.Name).ToList());
                    }
                }
                else if (entity is ReportTask)
                {
                    e.Node.Text = Helper.GetUniqueName(e.Label, (from i in Report.Tasks select i.Name).ToList());
                }
                else if (entity is ReportOutput)
                {
                    e.Node.Text = Helper.GetUniqueName(e.Label, (from i in Report.Outputs select i.Name).ToList());
                }
                else if (entity is ReportSchedule)
                {
                    e.Node.Text = Helper.GetUniqueName(e.Label, (from i in Report.Schedules select i.Name).ToList());
                    Report.SchedulesModified = true;
                }

                if (entity is RootComponent)
                {
                    ((RootComponent)entity).Name = e.Node.Text;
                    if (!(entity is ReportView)) mainTreeView.Sort();
                    if (entity is ReportSchedule) ((ReportSchedule)entity).SynchronizeTask();
                    SetModified();
                }
            }
        }

        #endregion

        #region Context Menu Handlers

        void addRemoveItem(string text)
        {
            if (treeContextMenuStrip.Items.Count > 0) treeContextMenuStrip.Items.Add(new ToolStripSeparator());
            removeToolStripMenuItem.Text = text;
            treeContextMenuStrip.Items.Add(removeToolStripMenuItem);

            string displayName = "";
            IList selectSource = treeViewHelper.getRemoveSource(ref displayName);
            removeToolStripMenuItem.Enabled = (selectSource.Count > 0);
        }

        void addAddItem(string text, object tag)
        {
            ToolStripMenuItem ts = new ToolStripMenuItem();
            ts.Click += new System.EventHandler(this.addToolStripMenuItem_Click);
            ts.Tag = tag;
            ts.Text = text;
            treeContextMenuStrip.Items.Add(ts);
        }

        void addRemoveRootItem(string text, object tag)
        {
            ToolStripMenuItem ts = new ToolStripMenuItem();
            ts.Click += new System.EventHandler(this.removeRootToolStripMenuItem_Click);
            ts.Tag = tag;
            ts.Text = text;
            if (treeContextMenuStrip.Items.Count > 0) treeContextMenuStrip.Items.Add(new ToolStripSeparator());
            treeContextMenuStrip.Items.Add(ts);
        }

        void addCopyItem(string text, object tag)
        {
            ToolStripMenuItem ts = new ToolStripMenuItem();
            ts.Click += new System.EventHandler(this.copyToolStripMenuItem_Click);
            ts.Tag = tag;
            ts.Text = text;
            if (treeContextMenuStrip.Items.Count > 0) treeContextMenuStrip.Items.Add(new ToolStripSeparator());
            treeContextMenuStrip.Items.Add(ts);
        }

        void addSmartCopyItem(string text, object tag)
        {
            ToolStripMenuItem ts = new ToolStripMenuItem();
            ts.Click += new System.EventHandler(this.smartCopyToolStripMenuItem_Click);
            ts.Tag = tag;
            ts.Text = text;
            if (treeContextMenuStrip.Items.Count > 0) treeContextMenuStrip.Items.Add(new ToolStripSeparator());
            treeContextMenuStrip.Items.Add(ts);
        }

        private void treeContextMenuStrip_Opening(object sender, CancelEventArgs e)
        {
            object entity = mainTreeView.SelectedNode.Tag;
            treeContextMenuStrip.Items.Clear();
            if (entity is SourceFolder)
            {
                addAddItem("Add Data Source", null);
                addAddItem("Add No SQL Data Source", null);
                foreach (var source in _repository.Sources)
                {
                    addAddItem(string.Format("Add {0} (Repository)", source.Name), source);
                }
                addRemoveItem("Remove Data Sources...");
            }
            else if (entity is ViewFolder)
            {
                if (_repository.ViewTemplates.Exists(i => i.Name == ReportViewTemplate.ModelHTMLName))
                {
                    addAddItem("Add a HTML View", null);
                }
                if (_repository.ViewTemplates.Exists(i => i.Name == ReportViewTemplate.ModelCSVExcelName))
                {
                    addAddItem("Add a CSV Excel View", null);
                }
                addRemoveItem("Remove Views...");
            }
            else if (entity is ReportView)
            {
                //Add only allowed template children names...and do not mix extensions
                string extension = ((ReportView)entity).Views.Max(i => i.Template.ExternalViewerExtension);
                var currentTemplateName = ((ReportView)entity).TemplateName;

                foreach (var template in _repository.ViewTemplates.Where(i => i.ParentNames.Contains(currentTemplateName) && (string.IsNullOrEmpty(extension) || i.ExternalViewerExtension == extension)))
                {
                    addAddItem("Add a " + template.Name + " View", template);
                }
                addRemoveItem("Remove Views...");
                addCopyItem("Copy " + ((RootComponent)entity).Name, entity);
                addRemoveRootItem("Remove " + ((RootComponent)entity).Name, entity);
                addSmartCopyItem("Smart copy...", entity);
            }
            else if (entity is TasksFolder)
            {
                addAddItem("Add a Task", null);
                addRemoveItem("Remove Tasks...");
                addSmartCopyItem("Smart copy...", entity);
            }
            else if (entity is OutputFolder)
            {
                foreach (var device in _repository.Devices)
                {
                    addAddItem("Add Output for " + device.FullName, device);
                }
                addRemoveItem("Remove Outputs...");
            }
            else if (entity is ScheduleFolder)
            {
                foreach (var output in _report.Outputs.OrderBy(i => i.Name))
                {
                    addAddItem("Add Schedule for " + output.Name, output);
                }
                if (_report.Tasks.Count > 0)
                {
                    if (treeContextMenuStrip.Items.Count > 0) treeContextMenuStrip.Items.Add(new ToolStripSeparator());
                    addAddItem("Add Schedule for the Report Tasks", null);
                }

                addRemoveItem("Remove Schedules...");
            }
            else if (entity is ReportModel)
            {
                addCopyItem("Copy " + ((RootComponent)entity).Name, entity);
                addRemoveRootItem("Remove " + ((RootComponent)entity).Name, entity);
                addSmartCopyItem("Smart copy...", entity);
            }
            else if (entity is ReportTask)
            {
                addCopyItem("Copy " + ((RootComponent)entity).Name, entity);
                addRemoveRootItem("Remove " + ((RootComponent)entity).Name, entity);
                addSmartCopyItem("Smart copy...", entity);
            }
            else if (entity is ReportOutput)
            {
                addCopyItem("Copy " + ((RootComponent)entity).Name, entity);
                addRemoveRootItem("Remove " + ((RootComponent)entity).Name, entity);
                addSmartCopyItem("Smart copy...", entity);
            }
            else if (entity is ReportSchedule)
            {
                addRemoveRootItem("Remove " + ((RootComponent)entity).Name, entity);
            }
            else if (entity is ReportSource)
            {
                addRemoveRootItem("Remove " + ((RootComponent)entity).Name, entity);

                if (treeContextMenuStrip.Items.Count > 0) treeContextMenuStrip.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem ts = new ToolStripMenuItem();
                ts.Click += new System.EventHandler(convertReportSourceAsRepositorySource);
                ts.Text = "Convert Report Source to a Repository Source...";
                ts.Enabled = (((ReportSource)entity).MetaSourceGUID == null);
                treeContextMenuStrip.Items.Add(ts);
                treeContextMenuStrip.Items.Add(new ToolStripSeparator());
                ts = new ToolStripMenuItem();
                ts.Click += new System.EventHandler(editMetaSource);
                ts.Text = "Edit the Repository Source with the Server Manager...";
                ts.Enabled = (((ReportSource)entity).MetaSourceGUID != null);
                treeContextMenuStrip.Items.Add(ts);
            }
            else
            {
                treeViewHelper.treeContextMenuStrip_Opening(sender, e);
            }
        }


        private void addToolStripMenuItem_Click(object sender, EventArgs e)
        {
            object newEntity = null;
            if (selectedEntity is SourceFolder && Report != null)
            {
                MetaSource source = ((ToolStripMenuItem)sender).Tag as MetaSource;
                ReportSource newSource = Report.AddSource(source);
                newSource.IsNoSQL = ((ToolStripMenuItem)sender).Text.Contains("No SQL");
                if (!newSource.IsNoSQL) newEntity = newSource.Connection;
                else newEntity = newSource.MetaData.MasterTable;
                if (source != null)
                {
                    newSource.LoadRepositoryMetaSources(_repository);
                    newEntity = newSource;
                }
            }
            else if (selectedEntity is ModelFolder)
            {
                newEntity = _report.AddModel();
            }
            else if (selectedEntity is ViewFolder)
            {
                newEntity = ((ToolStripMenuItem)sender).Text.Contains("CSV") ? _report.AddModelCSVView() : _report.AddModelHTMLView();
            }
            else if (selectedEntity is ReportView)
            {
                newEntity = _report.AddChildView((ReportView)selectedEntity, (ReportViewTemplate)((ToolStripMenuItem)sender).Tag);
            }
            else if (selectedEntity is TasksFolder)
            {
                ToolStripMenuItem menuItem = sender as ToolStripMenuItem;
                if (menuItem != null) newEntity = _report.AddTask();
            }
            else if (selectedEntity is OutputFolder)
            {
                ToolStripMenuItem menuItem = sender as ToolStripMenuItem;
                if (menuItem != null) newEntity = _report.AddOutput((OutputDevice)menuItem.Tag);
            }
            else if (selectedEntity is ScheduleFolder)
            {
                ToolStripMenuItem menuItem = sender as ToolStripMenuItem;
                if (menuItem != null) newEntity = _report.AddSchedule((ReportOutput)menuItem.Tag);
            }
            else
            {
                newEntity = treeViewHelper.addToolStripMenuItem_Click(sender, e);
            }

            if (newEntity != null)
            {
                IsModified = true;
                init(newEntity);
            }
        }

        private void sortColumnsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                Cursor.Current = Cursors.WaitCursor;
                treeViewHelper.sortColumns_Click(sender, e, sender == sortColumnSQLOrderToolStripMenuItem);
                IsModified = true;
                mainTreeView.Sort();
            }
            finally
            {
                Cursor.Current = Cursors.Default;
            }
        }

        private void convertReportSourceAsRepositorySource(object sender, EventArgs e)
        {
            ReportSource source = selectedEntity as ReportSource;
            if (source != null)
            {
                if (MessageBox.Show("You are about to save the Report Source into a Repository Source file and convert the report to use this new Repository Source.\r\nDo you want to continue ?", "Warning", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.Cancel) return;
                string path = ToolsHelper.SaveConfigurationFile(_repository.SourcesFolder, "", source.Name);
                if (string.IsNullOrEmpty(path)) return;

                //Create and save a meta source
                MetaSource metaSource = MetaSource.Create(_repository);
                metaSource.IsNoSQL = source.IsNoSQL;
                metaSource.InitScript = source.InitScript;
                metaSource.TasksScript = source.TasksScript;
                metaSource.Connections.Clear();
                metaSource.Connections.AddRange(source.Connections);
                metaSource.MetaData.Joins.AddRange(source.MetaData.Joins);
                metaSource.MetaData.Tables.Clear();
                metaSource.MetaData.Tables.AddRange(source.MetaData.Tables.Where(i => !i.IsMasterTable || source.IsNoSQL));
                metaSource.MetaData.Enums.AddRange(source.MetaData.Enums);
                metaSource.ConnectionGUID = source.ConnectionGUID;
                metaSource.PreSQL = source.PreSQL;
                metaSource.PostSQL = source.PostSQL;
                metaSource.IgnorePrePostError = source.IgnorePrePostError;
                metaSource.SaveToFile(path);

                _repository.Sources.Add(metaSource);

                //convert the report source to the metasource
                source.MetaSourceGUID = metaSource.GUID;
                source.Connections.Clear();
                source.MetaData.Joins.Clear();
                source.MetaData.Tables.Clear();
                source.MetaData.Enums.Clear();
                source.Name += " (Repository)";
                source.LoadRepositoryMetaSources(_repository);
                IsModified = true;
                init(source);
            }
        }


        private void editMetaSource(object sender, EventArgs e)
        {
            ReportSource source = selectedEntity as ReportSource;
            string path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), Repository.SealServerManager);
#if DEBUG
            path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath) + string.Format(@"\..\..\..\{0}\bin\Debug", Path.GetFileNameWithoutExtension(Repository.SealServerManager)), Repository.SealServerManager);
#endif
            MetaSource metaSource = _repository.Sources.FirstOrDefault(i => i.GUID == source.MetaSourceGUID);
            if (metaSource != null) Process.Start(path, string.Format("/o {0}", Helper.QuoteDouble(metaSource.FilePath)));
        }

        private void copyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            object newEntity = null;
            if (selectedEntity is ReportModel)
            {
                newEntity = Helper.Clone(selectedEntity);
                _report.Models.Add((ReportModel)newEntity);
                _report.InitReferences();
                ((RootComponent)newEntity).GUID = Guid.NewGuid().ToString();
                ReportModel model = (ReportModel)newEntity;
                foreach (var item in model.Elements) item.GUID = Guid.NewGuid().ToString();
                foreach (var item in model.Restrictions)
                {
                    string oldGUID = item.GUID;
                    item.GUID = Guid.NewGuid().ToString();
                    model.Restriction = model.Restriction.Replace(oldGUID, item.GUID);
                }
                ((RootComponent)newEntity).Name = Helper.GetUniqueName(((RootComponent)selectedEntity).Name + " - Copy", (from i in _report.Models select i.Name).ToList());
            }
            else if (selectedEntity is ReportView)
            {
                ReportView parent = mainTreeView.SelectedNode.Parent.Tag as ReportView;
                List<ReportView> views = (parent == null ? _report.Views : parent.Views);
                newEntity = Helper.Clone(selectedEntity);
                views.Add((ReportView)newEntity);
                _report.InitReferences();
                ((RootComponent)newEntity).GUID = Guid.NewGuid().ToString();
                ((ReportView)newEntity).ReinitGUIDChildren();
                ((RootComponent)newEntity).Name = Helper.GetUniqueName(((RootComponent)selectedEntity).Name + " - Copy", (from i in views select i.Name).ToList());
            }
            else if (selectedEntity is ReportTask)
            {
                newEntity = Helper.Clone(selectedEntity);
                _report.Tasks.Add((ReportTask)newEntity);
                _report.InitReferences();
                ((RootComponent)newEntity).GUID = Guid.NewGuid().ToString();
                ((RootComponent)newEntity).Name = Helper.GetUniqueName(((RootComponent)selectedEntity).Name + " - Copy", (from i in _report.Tasks select i.Name).ToList());
            }
            else if (selectedEntity is ReportOutput)
            {
                newEntity = Helper.Clone(selectedEntity);
                _report.Outputs.Add((ReportOutput)newEntity);
                _report.InitReferences();
                ((RootComponent)newEntity).GUID = Guid.NewGuid().ToString();
                ((RootComponent)newEntity).Name = Helper.GetUniqueName(((RootComponent)selectedEntity).Name + " - Copy", (from i in _report.Outputs select i.Name).ToList());
            }
            /* not useful unless we copy also the Task Definition....
            else if (selectedEntity is ReportSchedule)
            {
                newEntity = _report.AddSchedule(((ReportSchedule)selectedEntity).Output);
                Helper.CopyProperties(selectedEntity, newEntity);
                ((ReportSchedule)newEntity).Task = null; //Force a new task to be created in the Task Scheduler..
                ((RootComponent)newEntity).GUID = Guid.NewGuid().ToString();
                ((RootComponent)newEntity).Name = Helper.GetUniqueName(((RootComponent)selectedEntity).Name + " - Copy", (from i in _report.Schedules select i.Name).ToList());
            }*/
            else
            {
                newEntity = treeViewHelper.copyToolStripMenuItem_Click(sender, e);
            }

            if (newEntity != null)
            {
                IsModified = true;
                init(newEntity);
            }
        }

        private void smartCopyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SmartCopyForm form = null;
            if (selectedEntity is ReportModel)
            {
                form = new SmartCopyForm("Smart copy of " + ((ReportModel)selectedEntity).Name, selectedEntity, _report);
                form.ShowDialog();
            }
            else if (selectedEntity is ReportView)
            {
                form = new SmartCopyForm("Smart copy of " + ((ReportView)selectedEntity).Name, selectedEntity, _report);
                form.ShowDialog();
            }
            else if (selectedEntity is ReportTask)
            {
                form = new SmartCopyForm("Smart copy of " + ((ReportTask)selectedEntity).Name, selectedEntity, _report);
                form.ShowDialog();
            }
            else if (selectedEntity is ReportOutput)
            {
                form = new SmartCopyForm("Smart copy of " + ((ReportOutput)selectedEntity).Name, selectedEntity, _report);
                form.ShowDialog();
            }
            else if (selectedEntity is TasksFolder)
            {
                form = new SmartCopyForm("Smart copy of Tasks Script", selectedEntity, _report);
                form.ShowDialog();
            }

            if (form != null && form.IsReportModified)
            {
                CannotRenderAnymore();
                SetModified();
                init();
                var lastEntity = selectedEntity;
                mainTreeView.Sort();
                if (lastEntity != null) selectNode(lastEntity);
            }
        }


        private void removeRootToolStripMenuItem_Click(object sender, EventArgs e)
        {
            object newEntity = null;
            if (selectedEntity is ReportModel)
            {
                _report.RemoveModel((ReportModel)selectedEntity);
                newEntity = mainTreeView.SelectedNode.Parent.Tag;
            }
            else if (selectedEntity is ReportView)
            {
                if (mainTreeView.SelectedNode.Parent != null && mainTreeView.SelectedNode.Parent.Tag is ReportView)
                {
                    _report.RemoveView((ReportView)mainTreeView.SelectedNode.Parent.Tag, (ReportView)selectedEntity);
                }
                else
                {
                    _report.RemoveView(null, (ReportView)selectedEntity);
                }
                newEntity = mainTreeView.SelectedNode.Parent.Tag;
            }
            else if (selectedEntity is ReportTask)
            {
                _report.RemoveTask((ReportTask)selectedEntity);
                newEntity = mainTreeView.SelectedNode.Parent.Tag;
            }
            else if (selectedEntity is ReportOutput)
            {
                _report.RemoveOutput((ReportOutput)selectedEntity);
                newEntity = mainTreeView.SelectedNode.Parent.Tag;
            }
            else if (selectedEntity is ReportSchedule)
            {
                _report.RemoveSchedule((ReportSchedule)selectedEntity);
                newEntity = mainTreeView.SelectedNode.Parent.Tag;
            }
            else if (selectedEntity is ReportSource)
            {
                _report.RemoveSource((ReportSource)selectedEntity);
                newEntity = mainTreeView.SelectedNode.Parent.Tag;
            }
            else
            {
                newEntity = treeViewHelper.removeRootToolStripMenuItem_Click(sender, e);
            }


            if (newEntity != null)
            {
                IsModified = true;
                init(newEntity);
            }
        }
        private void removeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                if (treeViewHelper.removeToolStripMenuItem_Click(sender, e))
                {
                    IsModified = true;
                    init();
                }
            }
            catch
            {
                IsModified = true;
                init();
                throw;
            }
        }


        private void addFromToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (treeViewHelper.addFromToolStripMenuItem_Click(sender, e))
            {
                IsModified = true;
                init();
            }
        }

        private void executeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            bool render = (sender == renderToolStripButton || sender == renderToolStripMenuItem || sender == renderViewOutputToolStripButton || sender == renderViewOutputToolStripMenuItem);
            string viewGUID = null, outputGUID = null;
            if (sender == renderViewOutputToolStripMenuItem || sender == executeViewOutputToolStripMenuItem || sender == renderViewOutputToolStripButton || sender == executeViewOutputToolStripButton)
            {
                if (selectedEntity is ReportOutput)
                {
                    outputGUID = ((ReportOutput)selectedEntity).GUID;
                }
                else if (selectedEntity is ReportView)
                {
                    //Get the parent view
                    TreeNode node = mainTreeView.SelectedNode;
                    while (!(node.Parent.Tag is ViewFolder)) node = node.Parent;
                    viewGUID = (node.Tag as ReportView).GUID;
                }
            }
            ExecuteReport(render, viewGUID, outputGUID);
        }

        public void ExecuteReport(bool render, string viewGUID, string outputGUID)
        {
            //commit panels
            if (selectedEntity is ReportModel) modelPanel.Commit();

            //check report integrity...
            if (_report.Models.Count == 0)
            {
                if (MessageBox.Show("This report has no Model and cannot be executed. Do you want to create a Model now ?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    IsModified = true;
                    init(_report.AddModel());
                }
                return;
            }
            if (_report.Views.Count == 0)
            {
                if (MessageBox.Show("This report has no View and cannot be executed. Do you want to create a View now ?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    IsModified = true;
                    init(_report.AddModelHTMLView());
                }
                return;
            }

            if (_reportViewer == null || !_reportViewer.Visible)
            {
                _reportViewer = new ReportViewerForm(false, Properties.Settings.Default.ShowScriptErrors);
            }
            _reportViewer.ViewReport(_report.Clone(), _repository, render, viewGUID, outputGUID, _report.FilePath);
            _canRender = true;
            FileHelper.PurgeTempApplicationDirectory();
        }

        private void mainPropertyGrid_PropertyValueChanged(object s, PropertyValueChangedEventArgs e)
        {
            if (treeViewHelper.mainPropertyGrid_PropertyValueChanged(s, e)) init();
            IsModified = true;
            enableControls();
        }

        private void dynamicColumnsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            treeViewHelper.dynamicColumnsToolStripMenuItem_Click(sender, e);
            IsModified = true;
            init();
        }

        #endregion

        #region Drag and drop
        private void mainTreeView_ItemDrag(object sender, ItemDragEventArgs e)
        {
            TreeNode node = e.Item as TreeNode;
            if (node != null && (node.Tag is ReportView || node.Tag is ReportTask))
            {
                mainTreeView.SelectedNode = node;
                DoDragDrop(e.Item, DragDropEffects.Move);
            }
        }

        private void mainTreeView_DragEnter(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            if (e.Data.GetDataPresent("System.Windows.Forms.TreeNode", false))
            {
                Point pt = ((TreeView)sender).PointToClient(new Point(e.X, e.Y));
                TreeNode targetNode = ((TreeView)sender).GetNodeAt(pt);
                if (targetNode != null && (targetNode.Tag is ReportView || targetNode.Tag is ReportTask))
                {
                    e.Effect = DragDropEffects.Move;
                }
            }
        }

        private void mainTreeView_DragDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("System.Windows.Forms.TreeNode", false))
            {
                TreeNode targetNode = ((TreeView)sender).GetNodeAt(((TreeView)sender).PointToClient(new Point(e.X, e.Y)));
                TreeNode sourceNode = (TreeNode)e.Data.GetData("System.Windows.Forms.TreeNode");
                if (sourceNode != null && targetNode != null && sourceNode.Parent != null && sourceNode.Tag is ReportView && targetNode.Tag is ReportView  /*&& (sourceNode.Parent.Tag is ReportView || sourceNode.Parent.Tag is ViewFolder)*/)
                {
                    ReportView sourceView = sourceNode.Tag as ReportView;
                    ReportView targetView = targetNode.Tag as ReportView;
                    if (sourceNode.Parent == targetNode.Parent)
                    {
                        //move the position
                        List<ReportView> views = (targetNode.Parent.Tag is ReportView) ? ((ReportView)targetNode.Parent.Tag).Views : _report.Views;
                        int index = 0;
                        foreach (var view in views.OrderBy(i => i.SortOrder))
                        {
                            if (view == targetView)
                            {
                                sourceView.SortOrder = index++;
                                targetView.SortOrder = index;
                                if (index == views.Count)
                                {
                                    sourceView.SortOrder = index;
                                    targetView.SortOrder = index - 1;
                                }
                            }
                            else if (view != sourceView)
                            {
                                view.SortOrder = index;
                            }
                            index++;
                        }
                        SetModified();
                        mainTreeView.Sort();
                        e.Effect = DragDropEffects.Move;
                    }
                    else if (sourceView.Template.ParentNames.Contains(targetView.Template.Name) && !sourceView.IsAncestorOf(targetView))
                    {
                        //move the parent
                        ReportView parent = sourceNode.Parent.Tag as ReportView;
                        parent.Views.Remove(sourceView);
                        targetView.Views.Add(sourceView);
                        SetModified();
                        init(sourceView);
                        e.Effect = DragDropEffects.Move;
                    }
                }
                else if (sourceNode != null && targetNode != null && sourceNode.Tag is ReportTask && targetNode.Tag is ReportTask)
                {
                    ReportTask sourceTask = sourceNode.Tag as ReportTask;
                    ReportTask targetTask = targetNode.Tag as ReportTask;
                    //move the position
                    int index = 0;
                    foreach (var task in Report.Tasks.OrderBy(i => i.SortOrder))
                    {
                        if (task == targetTask)
                        {
                            sourceTask.SortOrder = index++;
                            targetTask.SortOrder = index;
                            if (index == Report.Tasks.Count)
                            {
                                sourceTask.SortOrder = index;
                                targetTask.SortOrder = index - 1;
                            }
                        }
                        else if (task != sourceTask)
                        {
                            task.SortOrder = index;
                        }
                        index++;
                    }
                    SetModified();
                    mainTreeView.Sort();
                    e.Effect = DragDropEffects.Move;
                }
            }
        }

        private void mainTreeView_DragOver(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;
            if (e.Data.GetDataPresent("System.Windows.Forms.TreeNode", false))
            {
                TreeNode targetNode = ((TreeView)sender).GetNodeAt(((TreeView)sender).PointToClient(new Point(e.X, e.Y)));
                TreeNode sourceNode = (TreeNode)e.Data.GetData("System.Windows.Forms.TreeNode");
                if (sourceNode != null && targetNode != null && sourceNode.Tag is ReportView && targetNode.Tag is ReportView)
                {
                    ReportView sourceView = sourceNode.Tag as ReportView;
                    ReportView targetView = targetNode.Tag as ReportView;
                    if (sourceNode.Parent == targetNode.Parent)
                    {
                        //move position
                        e.Effect = DragDropEffects.Move;
                    }
                    else if (sourceView.Template.ParentNames.Contains(targetView.Template.Name))
                    {
                        //move parent, check that the source if not a parent of the target
                        if (!sourceView.IsAncestorOf(targetView)) e.Effect = DragDropEffects.Move;
                    }
                }
                else if (sourceNode != null && targetNode != null && sourceNode.Tag is ReportTask && targetNode.Tag is ReportTask)
                {
                    if (sourceNode.Parent == targetNode.Parent)
                    {
                        //move position
                        e.Effect = DragDropEffects.Move;
                    }
                }
            }
        }

        private void mainTimer_Tick(object sender, EventArgs e)
        {
            executeToolStripMenuItem.Enabled = (_report != null && (_reportViewer == null || (_reportViewer != null && _reportViewer.CanExecute)));
            executeToolStripButton.Enabled = executeToolStripMenuItem.Enabled;
            renderToolStripMenuItem.Enabled = (_canRender && _report != null && _reportViewer != null && _reportViewer.Visible && _reportViewer.CanRender);
            renderToolStripButton.Enabled = renderToolStripMenuItem.Enabled;

            executeViewOutputToolStripMenuItem.Enabled = executeToolStripMenuItem.Enabled;
            renderViewOutputToolStripMenuItem.Enabled = renderToolStripMenuItem.Enabled;
            executeViewOutputToolStripButton.Enabled = executeToolStripMenuItem.Enabled;
            renderViewOutputToolStripButton.Enabled = renderToolStripMenuItem.Enabled;
        }

        private void ReportDesigner_KeyDown(object sender, KeyEventArgs e)
        {
            toolStripHelper.HandleShortCut(e);
        }

        #endregion
    }

}
