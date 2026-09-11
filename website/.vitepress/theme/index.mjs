import DefaultTheme from 'vitepress/theme-without-fonts'
import MermaidDiagram from './MermaidDiagram.vue'
import './style.css'

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('MermaidDiagram', MermaidDiagram)
  }
}
