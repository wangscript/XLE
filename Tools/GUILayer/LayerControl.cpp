// Copyright 2015 XLGAMES Inc.
//
// Distributed under the MIT License (See
// accompanying file "LICENSE" or the website
// http://www.opensource.org/licenses/mit-license.php)

#pragma warning(disable:4793) //  : function compiled as native :

#include "LayerControl.h"
#include "EngineControlInternal.h"
#include "IWindowRig.h"
#include "UITypesBinding.h"
#include "../../PlatformRig/InputTranslator.h"
#include "../../PlatformRig/PlatformRigUtil.h"
#include "../../PlatformRig/FrameRig.h"
#include "../../PlatformRig/OverlaySystem.h"
#include "../../PlatformRig/ModelVisualisation.h"
#include "../../PlatformRig/ManipulatorsUtil.h"
#include "../../PlatformRig/BasicManipulators.h"

#include "../../RenderOverlays/DebuggingDisplay.h"
#include "../../RenderOverlays/Font.h"
#include "../../SceneEngine/SceneEngineUtility.h"
#include "../../SceneEngine/LightingParserStandardPlugin.h"
#include "../../SceneEngine/LightingParserContext.h"
#include "../../RenderCore/Metal/DeviceContext.h"
#include "../../Utility/PtrUtils.h"
#include <stack>

unsigned FrameRenderCount = 0;

namespace GUILayer 
{
    class LayerControlPimpl 
    {
    public:
        std::shared_ptr<SceneEngine::LightingParserStandardPlugin> _stdPlugin;
        std::shared_ptr<PlatformRig::GlobalTechniqueContext> _globalTechniqueContext;
    };

    static PlatformRig::FrameRig::RenderResult RenderFrame(
        RenderCore::IThreadContext& context,
        LayerControlPimpl& pimpl,
        PlatformRig::IOverlaySystem* overlaySys)
    {
        using namespace SceneEngine;

        LightingParserContext lightingParserContext(*pimpl._globalTechniqueContext);
        lightingParserContext._plugins.push_back(pimpl._stdPlugin);

        if (overlaySys) {
            overlaySys->RenderToScene(&context, lightingParserContext);
        }

        ///////////////////////////////////////////////////////////////////////
        bool hasPendingResources = !lightingParserContext._pendingResources.empty();
        if (hasPendingResources) {
            auto metalContext = RenderCore::Metal::DeviceContext::Get(context);
            auto defaultFont0 = RenderOverlays::GetX2Font("Raleway", 16);
            DrawPendingResources(metalContext.get(), lightingParserContext, defaultFont0.get());
        }
        ///////////////////////////////////////////////////////////////////////

        if (overlaySys) {
            overlaySys->RenderWidgets(&context, lightingParserContext.GetProjectionDesc());
        }

        return PlatformRig::FrameRig::RenderResult(hasPendingResources);
    }

    void LayerControl::Render(RenderCore::IThreadContext& threadContext, IWindowRig& windowRig)
    {
        auto& frameRig = windowRig.GetFrameRig();
        frameRig.ExecuteFrame(
            threadContext, windowRig.GetPresentationChain().get(), 
            nullptr, nullptr,
            std::bind(
                RenderFrame, std::placeholders::_1,
                std::ref(*_pimpl), frameRig.GetMainOverlaySystem().get()));
    }

    static std::shared_ptr<PlatformRig::ModelVisCache> s_visCache;

///////////////////////////////////////////////////////////////////////////////////////////////////

    class ManipulatorStack : public RenderOverlays::DebuggingDisplay::IInputListener
    {
    public:
        bool    OnInputEvent(const RenderOverlays::DebuggingDisplay::InputSnapshot& evnt);
        void    Register(uint64 id, std::shared_ptr<Tools::IManipulator> manipulator);

        static const uint64 CameraManipulator = 256;

        ManipulatorStack();
        ~ManipulatorStack();
    protected:
        std::vector<std::shared_ptr<Tools::IManipulator>> _activeManipulators;
        std::vector<std::pair<uint64, std::shared_ptr<Tools::IManipulator>>> _registeredManipulators;
    };

    bool    ManipulatorStack::OnInputEvent(const RenderOverlays::DebuggingDisplay::InputSnapshot& evnt)
    {
        if (evnt.IsPress_MButton()) {
            auto i = LowerBound(_registeredManipulators, CameraManipulator);
            if (i!=_registeredManipulators.end() && i->first == CameraManipulator) {
                    // remove this manipulator if it already is on the active manipulators list...
                auto e = std::find(_activeManipulators.begin(), _activeManipulators.end(), i->second);
                if (e!=_activeManipulators.end()) { _activeManipulators.erase(e); }

                _activeManipulators.push_back(i->second);
            }
        }

        if (!_activeManipulators.empty()) {
            bool r = _activeManipulators[_activeManipulators.size()-1]->OnInputEvent(
                evnt,
                *(const SceneEngine::IntersectionTestContext*)nullptr,
                *(const SceneEngine::IntersectionTestScene*)nullptr);

            if (!r) { 
                _activeManipulators.erase(_activeManipulators.begin() + (_activeManipulators.size()-1));
            }
        }

        return false;
    }

    void    ManipulatorStack::Register(uint64 id, std::shared_ptr<Tools::IManipulator> manipulator)
    {
        auto i = LowerBound(_registeredManipulators, id);
        if (i!=_registeredManipulators.end() && i->first == id) {
            i->second = manipulator;
        } else {
            _registeredManipulators.insert(i, std::make_pair(id, std::move(manipulator)));
        }
    }

    ManipulatorStack::ManipulatorStack() {}
    ManipulatorStack::~ManipulatorStack()
    {}

///////////////////////////////////////////////////////////////////////////////////////////////////

    class InputLayer : public PlatformRig::IOverlaySystem
    {
    public:
        std::shared_ptr<IInputListener> GetInputListener();

        void RenderToScene(
            RenderCore::IThreadContext* context, 
            SceneEngine::LightingParserContext& parserContext); 
        void RenderWidgets(
            RenderCore::IThreadContext* context, 
            const RenderCore::Techniques::ProjectionDesc& projectionDesc);
        void SetActivationState(bool newState);

        InputLayer(std::shared_ptr<IInputListener> listener);
        ~InputLayer();
    protected:
        std::shared_ptr<IInputListener> _listener;
    };

    auto InputLayer::GetInputListener() -> std::shared_ptr<IInputListener>
    {
        return _listener;
    }

    void InputLayer::RenderToScene(
        RenderCore::IThreadContext*, 
        SceneEngine::LightingParserContext&) {}
    void InputLayer::RenderWidgets(
        RenderCore::IThreadContext*, 
        const RenderCore::Techniques::ProjectionDesc&) {}
    void InputLayer::SetActivationState(bool) {}

    InputLayer::InputLayer(std::shared_ptr<IInputListener> listener) : _listener(listener) {}
    InputLayer::~InputLayer() {}

///////////////////////////////////////////////////////////////////////////////////////////////////
    
    void LayerControl::SetupDefaultVis(ModelVisSettings^ settings)
    {
        if (!s_visCache) {
            s_visCache = std::make_shared<PlatformRig::ModelVisCache>(
                std::shared_ptr<RenderCore::Assets::IModelFormat>());
        }

        auto visLayer = std::make_unique<PlatformRig::ModelVisLayer>(settings->GetUnderlying(), s_visCache);
        auto& overlaySet = *GetWindowRig().GetFrameRig().GetMainOverlaySystem();
        overlaySet.AddSystem(std::move(visLayer));
        overlaySet.AddSystem(std::make_shared<PlatformRig::VisualisationOverlay>(settings->GetUnderlying()));

            // create an input listener that feeds into a stack of manipulators
        auto manipulators = std::make_unique<ManipulatorStack>();
        manipulators->Register(
            ManipulatorStack::CameraManipulator,
            PlatformRig::CreateCameraManipulator(settings->GetUnderlying()->_camera));

        overlaySet.AddSystem(std::make_shared<InputLayer>(std::move(manipulators)));
    }

    LayerControl::LayerControl()
    {
        _pimpl.reset(new LayerControlPimpl());
        _pimpl->_stdPlugin = std::make_shared<SceneEngine::LightingParserStandardPlugin>();
        _pimpl->_globalTechniqueContext = std::make_shared<PlatformRig::GlobalTechniqueContext>();
    }

	LayerControl::~LayerControl()
    {}
}
