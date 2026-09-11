import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { EtiquetasServico } from '../../nucleo/servicos/etiquetas.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { EtiquetaDto } from '../../nucleo/modelos';
import { textoSobre } from '../../nucleo/cor';

/** O VOCABULÁRIO DE ETIQUETAS.
 *
 *  Rótulos livres que a empresa cola nos cards — "Revendedor", "Urgente", "Inadimplente". Um
 *  vocabulário só: não existe etiqueta de venda e etiqueta de pós-venda, a mesma "Urgente" serve
 *  a qualquer card.
 *
 *  ===================== POR QUE SÓ O DONO CRIA =====================
 *  Mesma regra de Etapas do funil: é configuração, e define como a empresa inteira nomeia as
 *  coisas. APLICAR a etiqueta é trabalho do dia e vai ser de qualquer papel — deixar o vendedor
 *  criar no meio do atendimento faz nascer "Revendedor", "revenda" e "Revendedores" na mesma
 *  semana, e o filtro por etiqueta passa a achar só um pedaço de cada busca.
 *  =================================================================
 *
 *  ===================== ESTA TELA AINDA NÃO APLICA NADA =====================
 *  Ela cria o vocabulário. O seletor que cola a etiqueta no card vem no próximo bloco, e será um
 *  componente compartilhado pelas três telas onde isso acontece — caixa de entrada, contato e
 *  funil —, seguindo o `nucleo/fechamento/modal-fechamento`, que já é usado exatamente por essas
 *  três.
 *  ========================================================================== */
@Component({
  selector: 'app-etiquetas',
  imports: [FormsModule],
  templateUrl: './etiquetas.html',
  styleUrl: './etiquetas.css'
})
export class Etiquetas implements OnInit {
  private servico = inject(EtiquetasServico);
  private toast = inject(ToastServico);

  /** Espelha `ServicoEtiquetas.MaximoEtiquetas`. Duplicado de propósito: a tela esconde o
   *  formulário ANTES de o dono digitar um nome e levar 400. O servidor continua decidindo —
   *  aqui é cortesia. */
  readonly maximo = 60;

  lista = signal<EtiquetaDto[]>([]);
  carregando = signal(true);
  erro = signal('');
  salvando = signal(false);

  fNome = signal('');
  fCor = signal('#5C8F6E');
  erroNovo = signal('');

  editandoId = signal<number | null>(null);
  eNome = signal('');
  eCor = signal('');

  /** A etiqueta cuja remoção está sendo confirmada. Sem destino, diferente de etapa: apagar não
   *  deixa contato órfão — ele só perde um rótulo. */
  removendo = signal<EtiquetaDto | null>(null);

  cheio = computed(() => this.lista().length >= this.maximo);

  /** O fundo do chip é escolhido no seletor do sistema, e amarelo claro com texto branco não se
   *  lê. O cálculo mora em `nucleo/cor` porque o seletor que cola a etiqueta no card vai precisar
   *  do mesmo — o chip é o mesmo desenho nos dois lugares. */
  textoSobre = textoSobre;

  ngOnInit() { this.carregar(); }

  carregar() {
    this.carregando.set(true);
    this.servico.listar().subscribe({
      next: l => { this.lista.set(l); this.carregando.set(false); this.erro.set(''); },
      error: () => {
        this.erro.set('Não foi possível carregar as etiquetas.');
        this.carregando.set(false);
      }
    });
  }

  // ---------------------------------------------------------------- criar
  criar() {
    const nome = this.fNome().trim();
    if (nome.length < 2) { this.erroNovo.set('Dê um nome à etiqueta.'); return; }

    this.salvando.set(true);
    this.erroNovo.set('');
    this.servico.criar(nome, this.fCor()).subscribe({
      next: () => {
        this.salvando.set(false);
        this.fNome.set('');
        this.toast.sucesso(`Etiqueta "${nome}" criada.`);
        this.carregar();
      },
      error: e => {
        this.salvando.set(false);
        // A mensagem do servidor é a que importa — ela distingue "já existe" de "cor inválida"
        // de "chegou no limite", e o dono precisa saber qual dos três foi.
        this.erroNovo.set(e.error?.erro ?? 'Não foi possível criar.');
      }
    });
  }

  // ---------------------------------------------------------------- editar
  editar(e: EtiquetaDto) {
    this.editandoId.set(e.id);
    this.eNome.set(e.nome);
    this.eCor.set(e.cor);
  }

  cancelarEdicao() { this.editandoId.set(null); }

  salvarEdicao(e: EtiquetaDto) {
    this.servico.atualizar(e.id, this.eNome().trim(), this.eCor()).subscribe({
      next: () => {
        this.editandoId.set(null);
        this.toast.sucesso('Etiqueta atualizada.');
        this.carregar();
      },
      error: err => this.toast.erro(err.error?.erro ?? 'Não foi possível salvar.')
    });
  }

  // ---------------------------------------------------------------- remover
  /** ⚠️ PERGUNTA ANTES, mesmo sendo reversível em dois cliques. Quando a etiqueta passar a colar
   *  em cards, apagar vai soltar todas as marcações de uma vez — e aí não é mais reversível. A
   *  confirmação nasce junto para o gesto não mudar de significado depois. */
  confirmarRemocao(e: EtiquetaDto) { this.removendo.set(e); }

  cancelarRemocao() { this.removendo.set(null); }

  remover() {
    const alvo = this.removendo();
    if (!alvo) return;

    this.servico.remover(alvo.id).subscribe({
      next: () => {
        this.removendo.set(null);
        this.toast.info(`Etiqueta "${alvo.nome}" apagada.`);
        this.carregar();
      },
      error: err => {
        this.removendo.set(null);
        this.toast.erro(err.error?.erro ?? 'Não foi possível apagar.');
      }
    });
  }
}
