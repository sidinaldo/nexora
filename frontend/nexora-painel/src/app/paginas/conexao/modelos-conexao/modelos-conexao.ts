import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Observable } from 'rxjs';
import { ConexaoServico } from '../../../nucleo/servicos/conexao.servico';
import { ToastServico } from '../../../nucleo/toast/toast.servico';
import { CategoriaModelo, ModeloMensagem, StatusModelo } from '../../../nucleo/modelos';
import { erroAo } from '../../../nucleo/erros';

/** OS TEMPLATES DE UM NÚMERO DA API OFICIAL (INT-XX).
 *
 *  Fora da janela de 24h, a Meta só deixa sair template que ela aprovou. Aqui o dono escreve o
 *  rascunho, manda para a revisão e acompanha a decisão.
 *
 *  ⚠️ A TELA NÃO DECIDE NADA. O nome que a Meta aceita, o que pode ser editado, o status e o motivo
 *  da recusa vêm do servidor; os botões só aparecem conforme o status que ele devolveu. */
@Component({
  selector: 'app-modelos-conexao',
  imports: [FormsModule],
  templateUrl: './modelos-conexao.html',
  styleUrl: './modelos-conexao.css'
})
export class ModelosConexao {
  private servico = inject(ConexaoServico);
  private toast = inject(ToastServico);

  conexaoId = input.required<number>();

  lista = signal<ModeloMensagem[]>([]);
  carregando = signal(true);
  erro = signal('');

  /** O formulário: aberto para criar (`editandoId` nulo) ou para editar um rascunho. */
  formularioAberto = signal(false);
  editandoId = signal<number | null>(null);
  fNome = signal('');
  fCategoria = signal<CategoriaModelo>('utility');
  fCorpo = signal('');
  salvando = signal(false);
  erroForm = signal('');

  /** O template com uma ação em andamento: os botões dele ficam desligados até a resposta. */
  ocupadoId = signal<number | null>(null);

  /** A lista FECHADA do servidor: são os únicos dados que o envio tem para preencher. */
  readonly variaveis = ['nome', 'empresa', 'vendedor'];

  constructor() {
    effect(() => {
      const id = this.conexaoId();
      untracked(() => this.carregar(id));
    });
  }

  carregar(id = this.conexaoId()) {
    this.carregando.set(true);
    this.servico.listarModelos(id).subscribe({
      next: l => { this.lista.set(l); this.carregando.set(false); this.erro.set(''); },
      error: e => {
        this.erro.set(e.error?.erro ?? 'Não foi possível carregar os templates.');
        this.carregando.set(false);
      }
    });
  }

  // ---------------------------------------------------------------- formulário
  novo() {
    this.editandoId.set(null);
    this.fNome.set('');
    this.fCategoria.set('utility');
    this.fCorpo.set('');
    this.erroForm.set('');
    this.formularioAberto.set(true);
  }

  editar(m: ModeloMensagem) {
    this.editandoId.set(m.id);
    this.fNome.set(m.nome);
    this.fCategoria.set(m.categoria);
    this.fCorpo.set(m.corpo);
    this.erroForm.set('');
    this.formularioAberto.set(true);
  }

  cancelar() {
    this.formularioAberto.set(false);
    this.editandoId.set(null);
  }

  /** Põe a variável onde está o cursor — digitar as chaves à mão é o jeito mais fácil de errar. */
  inserir(variavel: string, campo: HTMLTextAreaElement) {
    const texto = this.fCorpo();
    const inicio = campo.selectionStart ?? texto.length;
    const fim = campo.selectionEnd ?? texto.length;
    const marcador = this.marcador(variavel);
    this.fCorpo.set(texto.slice(0, inicio) + marcador + texto.slice(fim));
    const posicao = inicio + marcador.length;
    setTimeout(() => { campo.focus(); campo.setSelectionRange(posicao, posicao); });
  }

  marcador(variavel: string): string { return '{{' + variavel + '}}'; }

  salvar() {
    if (!this.fNome().trim()) { this.erroForm.set('Dê um nome ao template.'); return; }
    if (!this.fCorpo().trim()) { this.erroForm.set('Escreva o texto do template.'); return; }

    const novo = { nome: this.fNome().trim(), categoria: this.fCategoria(), corpo: this.fCorpo() };
    const id = this.editandoId();
    // Os dois devolvem coisas diferentes, e aqui so importa se deu certo.
    const pedido: Observable<unknown> = id === null
      ? this.servico.criarModelo(this.conexaoId(), novo)
      : this.servico.editarModelo(id, novo);

    this.salvando.set(true);
    this.erroForm.set('');
    pedido.subscribe({
      next: () => {
        this.salvando.set(false);
        this.cancelar();
        this.toast.sucesso('Rascunho salvo. Quando estiver pronto, envie para a revisão da Meta.');
        this.carregar();
      },
      error: e => {
        this.salvando.set(false);
        this.erroForm.set(e.error?.erro ?? 'Não foi possível salvar o template.');
      }
    });
  }

  // ---------------------------------------------------------------- ações
  enviarParaRevisao(m: ModeloMensagem) {
    this.ocupadoId.set(m.id);
    this.servico.submeterModelo(m.id).subscribe({
      next: r => {
        this.ocupadoId.set(null);
        this.substituir(r);
        this.toast.sucesso(r.status === 'aprovado'
          ? 'A Meta já aprovou este template.'
          : 'Enviado para a revisão da Meta. A resposta costuma levar de minutos a um dia.');
      },
      error: e => {
        this.ocupadoId.set(null);
        this.toast.erro(e.error?.erro ?? 'Não foi possível enviar à Meta.');
      }
    });
  }

  atualizar(m: ModeloMensagem) {
    this.ocupadoId.set(m.id);
    this.servico.atualizarModelo(m.id).subscribe({
      next: r => { this.ocupadoId.set(null); this.substituir(r); },
      error: e => {
        this.ocupadoId.set(null);
        this.toast.erro(e.error?.erro ?? 'Não foi possível consultar a Meta.');
      }
    });
  }

  excluir(m: ModeloMensagem) {
    if (!confirm(`Apagar o template "${m.nome}"?`)) return;

    this.ocupadoId.set(m.id);
    this.servico.excluirModelo(m.id).subscribe({
      next: () => {
        this.ocupadoId.set(null);
        this.lista.update(l => l.filter(x => x.id !== m.id));
      },
      error: e => {
        this.ocupadoId.set(null);
        this.toast.erro(erroAo(e, 'apagar o template'));
      }
    });
  }

  private substituir(m: ModeloMensagem) {
    this.lista.update(l => l.map(x => x.id === m.id ? m : x));
  }

  // ---------------------------------------------------------------- rótulos
  rotuloStatus(s: StatusModelo): string {
    if (s === 'rascunho') return 'Rascunho';
    if (s === 'enviado') return 'Em revisão na Meta';
    if (s === 'aprovado') return 'Aprovado';
    return 'Recusado';
  }

  rotuloCategoria(c: CategoriaModelo): string {
    if (c === 'utility') return 'Utilidade';
    if (c === 'marketing') return 'Marketing';
    return 'Autenticação';
  }
}
