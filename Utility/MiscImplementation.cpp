// Copyright 2015 XLGAMES Inc.
//
// Distributed under the MIT License (See
// accompanying file "LICENSE" or the website
// http://www.opensource.org/licenses/mit-license.php)

#include "MemoryUtils.h"

namespace Utility
{
    void AlignedDeletor::operator()(void* ptr) 
    { 
        XlMemAlignFree(ptr); 
    }
}

