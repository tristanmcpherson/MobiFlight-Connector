import React, { createContext, useContext } from "react"

interface ToastContextType {
  toastId: string | number
  dismiss: () => void
}

const ToastContext = createContext<ToastContextType | null>(null)

export const ToastProvider = ({ 
  children, 
  toastId, 
  dismiss 
}: { 
  children: React.ReactNode
  toastId: string | number
  dismiss: () => void
}) => {
  return (
    <ToastContext.Provider value={{ toastId, dismiss }}>
      {children}
    </ToastContext.Provider>
  )
}

export const useToastContext = () => {
  const context = useContext(ToastContext)
  if (!context) {
    throw new Error('useToastContext must be used within a ToastProvider')
  }
  return context
}