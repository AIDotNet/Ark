import { AnimatePresence, motion, type Transition, type Variants } from "motion/react"
import { cn } from "cn"

/* ---------------- 共享缓动 / 弹簧 ---------------- */

/** 标准 out-quart 缓动：进出场默认手感，快出缓停 */
export const easeOutQuart = [0.25, 1, 0.5, 1] as const

/** 轻弹簧：小元素位移（stepper 胶囊、徽标） */
export const springSoft: Transition = { type: "spring", stiffness: 480, damping: 38, mass: 0.9 }

/* ---------------- Collapse：高度自适应展开/收起 ---------------- */

/**
 * 高度动画折叠容器：展开 height 0→auto、收起 auto→0，附带淡入淡出。
 * 用于树形子节点、可展开 SQL、条件渲染的选项区块。
 * AnimatePresence initial={false}：首次以展开态挂载时不播动画，点击展开才播。
 */
export function Collapse({
  open,
  children,
  className,
  duration = 0.22,
}: {
  open: boolean
  children: React.ReactNode
  className?: string
  duration?: number
}) {
  return (
    <AnimatePresence initial={false}>
      {open && (
        <motion.div
          data-slot="collapse"
          className={cn("overflow-hidden", className)}
          initial={{ height: 0, opacity: 0 }}
          animate={{ height: "auto", opacity: 1 }}
          exit={{ height: 0, opacity: 0 }}
          transition={{ duration, ease: easeOutQuart }}
        >
          {children}
        </motion.div>
      )}
    </AnimatePresence>
  )
}

/* ---------------- Stagger：列表级联入场 ---------------- */

const staggerContainer: Variants = {
  hidden: {},
  show: { transition: { staggerChildren: 0.045, delayChildren: 0.02 } },
}

const staggerItem: Variants = {
  hidden: { opacity: 0, y: 8 },
  show: { opacity: 1, y: 0, transition: { duration: 0.28, ease: easeOutQuart } },
}

/** 级联容器：直接子项需为 <StaggerItem>；仅在挂载时播一次，数据刷新不会重播 */
export function Stagger({ children, className }: { children: React.ReactNode; className?: string }) {
  return (
    <motion.div
      data-slot="stagger"
      className={className}
      variants={staggerContainer}
      initial="hidden"
      animate="show"
    >
      {children}
    </motion.div>
  )
}

export function StaggerItem({ children, className }: { children: React.ReactNode; className?: string }) {
  return (
    <motion.div data-slot="stagger-item" className={className} variants={staggerItem}>
      {children}
    </motion.div>
  )
}

/* ---------------- Rise：单元素入场（上浮 + 淡入） ---------------- */

export function Rise({
  children,
  className,
  delay = 0,
  y = 8,
}: {
  children: React.ReactNode
  className?: string
  delay?: number
  y?: number
}) {
  return (
    <motion.div
      className={className}
      initial={{ opacity: 0, y }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.28, ease: easeOutQuart, delay }}
    >
      {children}
    </motion.div>
  )
}

/* ---------------- 向导步骤切换：方向感知滑动 ---------------- */

/** custom 传方向（1=前进 / -1=后退），前进左滑入、后退右滑入 */
export const stepVariants: Variants = {
  enter: (dir: number) => ({ opacity: 0, x: dir >= 0 ? 36 : -36 }),
  center: { opacity: 1, x: 0 },
  exit: (dir: number) => ({ opacity: 0, x: dir >= 0 ? -36 : 36 }),
}

export const stepTransition: Transition = { duration: 0.24, ease: easeOutQuart }
