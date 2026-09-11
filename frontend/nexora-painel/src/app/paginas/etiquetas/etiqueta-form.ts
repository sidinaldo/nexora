import {
  Component, ElementRef, Injector, OnInit, ViewChild, afterNextRender, computed, inject, input,
  output, signal
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { textoSobre } from '../../nucleo/cor';

export interface ValorEtiqueta { nome: string; cor: string; }

/** Nome mínimo e máximo, espelhando `ServicoEtiquetas`. Duplicado de propósito: a tela recusa
 *  antes de gastar uma ida ao servidor. Quem decide continua sendo ele. */
export const NOME_MINIMO = 2;
export const NOME_MAXIMO = 30;

/** O FORMULÁRIO DE ETIQUETA — um só, para criar e para editar.
 *
 *  ===================== POR QUE UNIFICAR =====================
 *  Eram dois blocos que faziam a mesma coisa e divergiram no primeiro dia: o de criar mostrava a
 *  pré-via do chip e validava o nome; o de editar não fazia nem um nem outro. Quem editava a cor
 *  de uma etiqueta descobria o resultado depois de salvar, e quem apagava o nome inteiro salvava
 *  vazio.
 *
 *  É o mesmo defeito que o `design-system.spec.ts` persegue no CSS — cópia de primitivo que
 *  diverge em silêncio —, só que em comportamento.
 *  ============================================================
 *
 *  ===================== O ERRO SÓ APARECE DEPOIS DA INTERAÇÃO =====================
 *  ⚠️ Isto é inédito no projeto: não há reactive forms em lugar nenhum, e nenhuma tela tem estado
 *  de "tocado". `tocado` e `enviado` são a versão em sinal de `control.touched || submitted`.
 *
 *  O motivo é que a mensagem certa na hora errada é ruído: abrir um formulário limpo e já ver
 *  "Dê um nome à etiqueta" é o sistema repreendendo alguém que ainda não fez nada. Depois de duas
 *  telas assim, o usuário para de ler as mensagens vermelhas — inclusive as que importam.
 *  =============================================================================== */
@Component({
  selector: 'app-etiqueta-form',
  imports: [FormsModule],
  templateUrl: './etiqueta-form.html',
  styleUrl: './etiqueta-form.css'
})
export class EtiquetaForm implements OnInit {
  modo = input.required<'criar' | 'editar'>();
  inicial = input<ValorEtiqueta>({ nome: '', cor: '#5C8F6E' });
  salvando = input(false);

  /** A mensagem que o servidor devolveu — "Já existe uma etiqueta chamada X". Fica abaixo do
   *  campo, e não numa caixa no rodapé: o erro é sobre o nome, e é ao lado do nome que quem
   *  digitou vai olhar. */
  erroServidor = input('');

  salvar = output<ValorEtiqueta>();
  cancelar = output<void>();

  nome = signal('');
  cor = signal('#5C8F6E');
  private tocado = signal(false);
  private enviado = signal(false);

  readonly maximo = NOME_MAXIMO;

  textoSobre = textoSobre;

  /** O input de nome. O foco vai para cá quando o formulário abre — sem isso, abrir a edição de
   *  uma etiqueta obriga quem usa teclado a percorrer a lista inteira até achar o campo. */
  @ViewChild('campoNome') private campoNome?: ElementRef<HTMLInputElement>;
  private injetor = inject(Injector);

  /** ⚠️ O VALOR INICIAL É COPIADO AQUI, E NÃO NO `afterNextRender` ABAIXO.
   *
   *  São duas coisas com tempos diferentes, e juntá-las foi um erro: copiar `inicial()` é estado,
   *  e precisa estar pronto na PRIMEIRA renderização — senão a edição desenha um campo vazio e
   *  quem estiver lendo (ou um teste) vê o formulário de editar sem o nome que ia editar. Dar
   *  foco é DOM, e só pode acontecer depois que o input existe.
   *
   *  `ngOnInit` é o primeiro momento em que `input()` já tem valor. */
  ngOnInit() {
    this.nome.set(this.inicial().nome);
    this.cor.set(this.inicial().cor);
  }

  constructor() {
    // ⚠️ `afterNextRender`, e não `setTimeout(0)`. O app é ZONELESS: o Angular agenda o render
    // com `requestAnimationFrame`, e um timer de 0 CORRE com ele — o `.focus()` às vezes acha o
    // input, às vezes acha `undefined`. Mesmo raciocínio documentado em `nucleo/thread/thread.ts`.
    afterNextRender(() => this.campoNome?.nativeElement.focus(), { injector: this.injetor });
  }

  // ---------------------------------------------------------------- validação
  /** Só o que a tela sabe julgar sozinha. Nome repetido e teto de 60 são do servidor — ele tem a
   *  lista inteira e a última palavra. */
  erroLocal = computed(() => {
    const n = this.nome().trim();
    if (n.length === 0) return 'Dê um nome à etiqueta.';
    if (n.length < NOME_MINIMO) return `O nome precisa de ao menos ${NOME_MINIMO} caracteres.`;
    return '';
  });

  valido = computed(() => this.erroLocal() === '');

  /** A regra toda desta tela em uma linha: cala enquanto ninguém interagiu. */
  mostrarErro = computed(() => (this.tocado() || this.enviado()) && this.erroLocal() !== '');

  /** O que o leitor de tela anuncia e o que aparece embaixo do campo. O do servidor vence: ele é
   *  mais específico ("Já existe uma etiqueta chamada VIP" diz mais que "dê um nome"). */
  erroVisivel = computed(() => this.erroServidor() || (this.mostrarErro() ? this.erroLocal() : ''));

  marcarTocado() { this.tocado.set(true); }

  // ---------------------------------------------------------------- ações
  enviar() {
    this.enviado.set(true);
    if (!this.valido()) {
      this.campoNome?.nativeElement.focus();
      return;
    }
    this.salvar.emit({ nome: this.nome().trim(), cor: this.cor() });
  }

  /** `Esc` desiste. É o gesto que todo mundo já tenta antes de procurar o botão Cancelar, e até o
   *  DES-XX não acontecia nada em tela nenhuma do projeto. */
  aoTeclar(evento: KeyboardEvent) {
    if (evento.key === 'Escape') {
      evento.preventDefault();
      this.cancelar.emit();
    }
  }
}
