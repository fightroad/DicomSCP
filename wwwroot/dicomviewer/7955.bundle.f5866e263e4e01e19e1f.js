"use strict";(self.webpackChunk=self.webpackChunk||[]).push([[7955],{27955:(e,o,a)=>{a.r(o),a.d(o,{default:()=>E});const t=JSON.parse('{"UU":"@ohif/mode-preclinical-4d"}').UU;var i=a(29463);const n={sopClassHandler:"@ohif/extension-cornerstone-dynamic-volume.sopClassHandlerModule.dynamic-volume",leftPanel:"@ohif/extension-cornerstone-dynamic-volume.panelModule.dynamic-volume",segmentation:"@ohif/extension-cornerstone-dynamic-volume.panelModule.dynamic-segmentation"},r={buttonSection:"primary",buttons:["MeasurementTools","Zoom","WindowLevel","Crosshairs","Pan"]},s=[[n.leftPanel,"@ohif/extension-cornerstone.panelModule.activeViewportWindowLevel"]],l={panels:{left:s,right:[]}};function d({servicesManager:e}){const{workflowStepsService:o}=e.services,a=function({servicesManager:e}){return{steps:[{id:"dataPreparation",name:"Data Preparation",layout:{panels:{left:s}},toolbarButtons:r,hangingProtocol:{protocolId:"default4D",stageId:"dataPreparation"},info:"In the Data Preparation step, you can visualize the dynamic PT volume data in three orthogonal views: axial, sagittal, and coronal. Use the left panel controls to adjust the visualization settings, such as playback speed, or navigate between different frames. This step allows you to assess the quality of the PT data and prepare for further analysis or registration with other modalities."},{id:"registration",name:"Registration",layout:l,toolbarButtons:r,hangingProtocol:{protocolId:"default4D",stageId:"registration"},info:"The Registration step provides a comprehensive view of the CT, PT, and fused CT-PT volume data in multiple orientations. The fusion viewports display the CT and PT volumes overlaid, allowing you to visually assess the alignment and registration between the two modalities. The individual CT and PT viewports are also available for side-by-side comparison. This step is crucial for ensuring proper registration before proceeding with further analysis or quantification."},{id:"roiQuantification",name:"ROI Quantification",layout:{panels:{left:s,right:[[n.segmentation]]},options:{leftPanelClosed:!1,rightPanelClosed:!1}},toolbarButtons:[r,{buttonSection:"dynamic-toolbox",buttons:["BrushTools","RectangleROIStartEndThreshold"]}],hangingProtocol:{protocolId:"default4D",stageId:"roiQuantification"},info:"The ROI quantification step allows you to define regions of interest (ROIs) with labelmap segmentations, on the fused CT-PT volume data using the labelmap tools. The left panel provides controls for adjusting the dynamic volume visualization, while the right panel offers tools for segmentation, editing, and exporting the ROI data. This step enables you to quantify the uptake or other measures within the defined ROIs for further analysis."},{id:"kineticAnalysis",name:"Kinetic Analysis",layout:l,toolbarButtons:r,hangingProtocol:{protocolId:"default4D",stageId:"kineticAnalysis"},onEnter:[{commandName:"updateSegmentationsChartDisplaySet",options:{servicesManager:e}}],info:"The Kinetic Analysis step provides a comprehensive view for visualizing and analyzing the dynamic data derived from the ROI segmentations. The fusion viewports display the combined CT-PT volume data, while a dedicated viewport shows a series chart representing the data over time. This step allows you to explore the temporal dynamics of the uptake or other kinetic measures within the defined regions of interest, enabling further quantitative analysis and modeling."}]}}({servicesManager:e});o.addWorkflowSteps(a.steps),o.setActiveWorkflowStep(a.steps[0].id)}const u={default:"dynamic4D-default",PT:"dynamic4D-pt",Fusion:"dynamic4D-fusion",CT:"dynamic4D-ct"},c={"viewport-0":"rgb(200, 0, 0)","viewport-1":"rgb(200, 200, 0)","viewport-2":"rgb(0, 200, 0)"},m={axial:"rgb(200, 0, 0)",sagittal:"rgb(200, 200, 0)",coronal:"rgb(0, 200, 0)"};function p({toolNames:e,Enums:o,toolGroupService:a,commandsManager:t,servicesManager:i}){!function(e,o,a,t,i){const{cornerstoneViewportService:n}=i.services,r={active:[{toolName:e.WindowLevel,bindings:[{mouseButton:o.MouseBindings.Primary}]},{toolName:e.Pan,bindings:[{mouseButton:o.MouseBindings.Auxiliary}]},{toolName:e.Zoom,bindings:[{mouseButton:o.MouseBindings.Secondary}]},{toolName:e.StackScroll,bindings:[{mouseButton:o.MouseBindings.Wheel}]}],passive:[{toolName:e.Length},{toolName:e.ArrowAnnotate},{toolName:e.Bidirectional},{toolName:e.Probe},{toolName:e.EllipticalROI},{toolName:e.RectangleROI},{toolName:e.RectangleROIThreshold},{toolName:e.RectangleScissors},{toolName:e.PaintFill},{toolName:e.StackScroll},{toolName:e.Magnify},{toolName:"CircularBrush",parentTool:"Brush",configuration:{activeStrategy:"FILL_INSIDE_CIRCLE",brushSize:7}},{toolName:"CircularEraser",parentTool:"Brush",configuration:{activeStrategy:"ERASE_INSIDE_CIRCLE",brushSize:7}},{toolName:"SphereBrush",parentTool:"Brush",configuration:{activeStrategy:"FILL_INSIDE_SPHERE",brushSize:7}},{toolName:"SphereEraser",parentTool:"Brush",configuration:{activeStrategy:"ERASE_INSIDE_SPHERE",brushSize:7}},{toolName:"ThresholdCircularBrush",parentTool:"Brush",configuration:{activeStrategy:"THRESHOLD_INSIDE_CIRCLE",brushSize:7}},{toolName:"ThresholdSphereBrush",parentTool:"Brush",configuration:{activeStrategy:"THRESHOLD_INSIDE_SPHERE",brushSize:7}},{toolName:e.CircleScissors},{toolName:e.RectangleScissors},{toolName:e.SphereScissors},{toolName:e.StackScroll},{toolName:e.Magnify}],enabled:[],disabled:[{toolName:e.Crosshairs,configuration:{viewportIndicators:!0,viewportIndicatorsConfig:{circleRadius:5,xOffset:.95,yOffset:.05},disableOnPassive:!0,autoPan:{enabled:!1,panSize:10},getReferenceLineColor:e=>{const o=n.getViewportInfo(e),a=o?.viewportOptions;return a?c[a.id]||m[a.orientation]||"#0c0":(console.warn("missing viewport?",e),"#0c0")}}}]};a.createToolGroupAndAddTools(u.PT,{...r,passive:[...r.passive,{toolName:"RectangleROIStartEndThreshold"}]}),a.createToolGroupAndAddTools(u.CT,{...r,passive:[...r.passive,{toolName:"RectangleROIStartEndThreshold"}]}),a.createToolGroupAndAddTools(u.Fusion,{...r,passive:[...r.passive,{toolName:"RectangleROIStartEndThreshold"}]}),a.createToolGroupAndAddTools(u.default,r)}(e,o,a,0,i)}const{createButton:h}=i.hx,v={commandName:"setToolActiveToolbar",commandOptions:{toolGroupIds:[u.PT,u.CT,u.Fusion,u.default]}},g=[{id:"MeasurementTools",uiType:"ohif.splitButton",props:{groupId:"MeasurementTools",evaluate:"evaluate.group.promoteToPrimaryIfCornerstoneToolNotActiveInTheList",primary:h({id:"Length",icon:"tool-length",label:"Length",tooltip:"Length Tool",commands:v,evaluate:"evaluate.cornerstoneTool"}),secondary:{icon:"chevron-down",tooltip:"More Measure Tools"},items:[{id:"Length",icon:"tool-length",label:"Length",tooltip:"Length Tool",commands:v,evaluate:"evaluate.cornerstoneTool"},{id:"Bidirectional",icon:"tool-bidirectional",label:"Bidirectional",tooltip:"Bidirectional Tool",commands:v,evaluate:"evaluate.cornerstoneTool"},{id:"ArrowAnnotate",icon:"tool-annotate",label:"Annotation",tooltip:"Arrow Annotate",commands:v,evaluate:"evaluate.cornerstoneTool"},{id:"EllipticalROI",icon:"tool-ellipse",label:"Ellipse",tooltip:"Ellipse ROI",commands:v,evaluate:"evaluate.cornerstoneTool"}]}},{id:"Zoom",uiType:"ohif.radioGroup",props:{icon:"tool-zoom",label:"Zoom",commands:v,evaluate:"evaluate.cornerstoneTool"}},{id:"WindowLevel",uiType:"ohif.radioGroup",props:{icon:"tool-window-level",label:"Window Level",commands:v,evaluate:"evaluate.cornerstoneTool"}},{id:"Pan",uiType:"ohif.radioGroup",props:{type:"tool",icon:"tool-move",label:"Pan",commands:v,evaluate:"evaluate.cornerstoneTool"}},{id:"TrackballRotate",uiType:"ohif.radioGroup",props:{type:"tool",icon:"tool-3d-rotate",label:"3D Rotate",commands:v,evaluate:"evaluate.cornerstoneTool"}},{id:"Capture",uiType:"ohif.radioGroup",props:{icon:"tool-capture",label:"Capture",commands:"showDownloadViewportModal",evaluate:["evaluate.action",{name:"evaluate.viewport.supported",unsupportedViewportTypes:["video","wholeSlide"]}]}},{id:"Layout",uiType:"ohif.layoutSelector",props:{rows:3,columns:4,evaluate:"evaluate.action"}},{id:"Crosshairs",uiType:"ohif.radioGroup",props:{type:"tool",icon:"tool-crosshair",label:"Crosshairs",commands:v,evaluate:"evaluate.cornerstoneTool"}},{id:"ProgressDropdown",uiType:"ohif.progressDropdown"},{id:"RectangleROIStartEndThreshold",uiType:"ohif.radioGroup",props:{icon:"tool-create-threshold",label:"Rectangle ROI Threshold",commands:v,evaluate:{name:"evaluate.cornerstone.segmentation",toolNames:["RectangleROIStartEndThreshold"]},options:"tmtv.RectangleROIThresholdOptions"}}],T=[{id:"BrushTools",uiType:"ohif.buttonGroup",props:{groupId:"BrushTools",items:[{id:"Brush",icon:"icon-tool-brush",label:"Brush",evaluate:{name:"evaluate.cornerstone.segmentation",toolNames:["CircularBrush","SphereBrush"]},options:[{name:"Size (mm)",id:"brush-radius",type:"range",min:.5,max:99.5,step:.5,value:7,commands:{commandName:"setBrushSize",commandOptions:{toolNames:["CircularBrush","SphereBrush"]}}},{name:"Shape",type:"radio",id:"brush-mode",value:"CircularBrush",values:[{value:"CircularBrush",label:"Circle"},{value:"SphereBrush",label:"Sphere"}],commands:"setToolActiveToolbar"}]},{id:"Eraser",icon:"icon-tool-eraser",label:"Eraser",evaluate:{name:"evaluate.cornerstone.segmentation",toolNames:["CircularEraser","SphereEraser"]},options:[{name:"Radius (mm)",id:"eraser-radius",type:"range",min:.5,max:99.5,step:.5,value:7,commands:{commandName:"setBrushSize",commandOptions:{toolNames:["CircularEraser","SphereEraser"]}}},{name:"Shape",type:"radio",id:"eraser-mode",value:"CircularEraser",values:[{value:"CircularEraser",label:"Circle"},{value:"SphereEraser",label:"Sphere"}],commands:"setToolActiveToolbar"}]},{id:"Threshold",icon:"icon-tool-threshold",label:"Eraser",evaluate:{name:"evaluate.cornerstone.segmentation",toolNames:["ThresholdCircularBrush","ThresholdSphereBrush"]},options:[{name:"Radius (mm)",id:"threshold-radius",type:"range",min:.5,max:99.5,step:.5,value:7,commands:{commandName:"setBrushSize",commandOptions:{toolNames:["ThresholdCircularBrush","ThresholdSphereBrush"]}}},{name:"Shape",type:"radio",id:"eraser-mode",value:"ThresholdCircularBrush",values:[{value:"ThresholdCircularBrush",label:"Circle"},{value:"ThresholdSphereBrush",label:"Sphere"}],commands:"setToolActiveToolbar"},{name:"ThresholdRange",type:"double-range",id:"threshold-range",min:0,max:100,step:.5,value:[2,50],commands:{commandName:"setThresholdRange",commandOptions:{toolNames:["ThresholdCircularBrush","ThresholdSphereBrush"]}}}]}]}},{id:"Shapes",uiType:"ohif.radioGroup",props:{label:"Shapes",evaluate:{name:"evaluate.cornerstone.segmentation",toolNames:["CircleScissor","SphereScissor","RectangleScissor"]},icon:"icon-tool-shape",options:[{name:"Shape",type:"radio",value:"CircleScissor",id:"shape-mode",values:[{value:"CircleScissor",label:"Circle"},{value:"SphereScissor",label:"Sphere"},{value:"RectangleScissor",label:"Rectangle"}],commands:"setToolActiveToolbar"}]}}],f={"@ohif/extension-default":"3.7.0-beta.76","@ohif/extension-cornerstone":"3.7.0-beta.76","@ohif/extension-cornerstone-dynamic-volume":"3.7.0-beta.76","@ohif/extension-cornerstone-dicom-seg":"3.7.0-beta.76","@ohif/extension-tmtv":"3.7.0-beta.76"},S="@ohif/extension-default.layoutTemplateModule.viewerLayout",y="@ohif/extension-default.sopClassHandlerModule.stack",b="@ohif/extension-default.sopClassHandlerModule.chart",w="@ohif/extension-default.viewportModule.chartViewport",C="@ohif/extension-cornerstone-dynamic-volume.panelModule.dynamic-volume",N="@ohif/extension-cornerstone.viewportModule.cornerstone",I="@ohif/extension-cornerstone.panelModule.activeViewportWindowLevel";const E={id:t,modeFactory:function({modeConfiguration:e}){return{id:t,routeName:"dynamic-volume",displayName:"Preclinical 4D",onModeEnter:function({servicesManager:e,extensionManager:o,commandsManager:a}){const{measurementService:t,toolbarService:i,cineService:n,cornerstoneViewportService:r,toolGroupService:s,customizationService:l,viewportGridService:d}=e.services,u=o.getModuleEntry("@ohif/extension-cornerstone.utilityModule.tools"),{toolNames:c,Enums:m}=u.exports;t.clearMeasurements(),p({toolNames:c,Enums:m,toolGroupService:s,commandsManager:a,servicesManager:e}),i.addButtons([...g,...T]),i.createButtonSection("secondary",["ProgressDropdown"]),l.addModeCustomizations([{id:"PanelSegmentation.tableMode",mode:"expanded"},{id:"PanelSegmentation.onSegmentationAdd",onSegmentationAdd:()=>{a.run("createNewLabelMapForDynamicVolume")}},{id:"PanelSegmentation.showAddSegment",showAddSegment:!1},{id:"PanelSegmentation.readableText",readableText:{lesionStats:"Lesion Statistics",minValue:"Minimum Value",maxValue:"Maximum Value",meanValue:"Mean Value (ml)",volume:"Volume",suvPeak:"SUV Peak",suvMax:"Maximum SUV",suvMaxIJK:"SUV Max IJK"}}]);const{unsubscribe:h}=r.subscribe(r.EVENTS.VIEWPORT_VOLUMES_CHANGED,(()=>{const e=d.getActiveViewportId(),o=r.getCornerstoneViewport(e);n.playClip(o.element,{viewportId:e}),h()}))},onSetupRouteComplete:({servicesManager:e})=>{d({servicesManager:e})},onModeExit:({servicesManager:e})=>{const{toolGroupService:o,syncGroupService:a,segmentationService:t,cornerstoneViewportService:i}=e.services;o.destroy(),a.destroy(),t.destroy(),i.destroy()},get validationTags(){return{study:[],series:[]}},isValidMode:({modalities:e,study:o})=>({valid:"M1"===o.mrn,description:"This mode is only available for 4D PET/CT studies."}),routes:[{path:"preclinical-4d",layoutTemplate:({location:e,servicesManager:o})=>({id:S,props:{leftPanels:[[C,I]],rightPanels:[],rightPanelClosed:!0,viewports:[{namespace:N,displaySetsToDisplay:[y]},{namespace:w,displaySetsToDisplay:[b]}]}})}],extensions:f,hangingProtocol:"default4D",sopClassHandlers:[b,y],hotkeys:[...i.ot.defaults.hotkeyBindings]}},extensionDependencies:f}}}]);
//# sourceMappingURL=7955.bundle.f5866e263e4e01e19e1f.js.map